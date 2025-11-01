// SPDX-FileCopyrightText: 2024 Julian Giebel <juliangiebel@live.de>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Pieter-Jan Briers <pieterjan.briers+git@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Discord;
using Content.Server.GameTicking;
using Robust.Shared.Player;
using Serilog;

namespace Content.Server.Administration.Managers;

public sealed partial class BanManager
{
    // Responsible for ban notification handling.
    // Ban notifications are sent through the database to notify the entire server group that a new ban has been added,
    // so that people will get kicked if they are banned on a different server than the one that placed the ban.
    //
    // Ban notifications are currently sent by a trigger in the database, automatically.

    /// <summary>
    /// The notification channel used to broadcast information about new bans.
    /// </summary>
    public const string BanNotificationChannel = "ban_notification";

    public const string UnbanNotificationChannel = "unban_notification";

    // Rate limit to avoid undue load from mass-ban imports.
    // Only process 10 bans per 30 second interval.
    //
    // I had the idea of maybe binning this by postgres transaction ID,
    // to avoid any possibility of dropping a normal ban by coincidence.
    // Didn't bother implementing this though.
    private static readonly TimeSpan BanNotificationRateLimitTime = TimeSpan.FromSeconds(30);
    private const int BanNotificationRateLimitCount = 10;

    private readonly object _banNotificationRateLimitStateLock = new();
    private TimeSpan _banNotificationRateLimitStart;
    private int _banNotificationRateLimitCount;

    private static readonly TimeSpan UnbanNotificationRateLimitTime = TimeSpan.FromSeconds(30);
    private const int UnbanNotificationRateLimitCount = 10;

    private readonly object _unbanNotificationRateLimitStateLock = new();
    private TimeSpan _unbanNotificationRateLimitStart;
    private int _unbanNotificationRateLimitCount;


    [GeneratedRegex(@"^https://(?:(?:canary|ptb)\.)?discord\.com/api/webhooks/(\d+)/((?!.*/).*)$")]
    private static partial Regex DiscordRegex();

    private string _webhookUrl = string.Empty;

    private readonly HttpClient _httpClient = new();

    private bool OnBanDatabaseNotificationEarlyFilter()
    {
        if (!CheckBanRateLimit())
        {
            _sawmill.Verbose("Not processing ban notification due to rate limit");
            return false;
        }

        return true;
    }

    private bool OnUnbanDatabaseNotificationEarlyFilter()
    {
        if (!CheckUnbanRateLimit())
        {
            _sawmill.Verbose("Not processing unban notification due to rate limit");
            return false;
        }

        return true;
    }

    private async void ProcessBanNotification(BanNotificationData data)
    {
        if ((await _entryManager.ServerEntity).Id == data.ServerId)
        {
            _sawmill.Verbose("Not processing ban notification: came from this server");
            return;
        }

        _sawmill.Verbose($"Processing ban notification for ban {data.BanId}");
        var ban = await _db.GetServerBanAsync(data.BanId);
        if (ban == null)
        {
            _sawmill.Warning($"Ban in notification ({data.BanId}) didn't exist?");
            return;
        }

        PostBanNotificationMessage(ban);
        KickMatchingConnectedPlayers(ban, "ban notification");
    }

    private async void ProcessUnbanNotification(UnbanNotificationData data)
    {
        _sawmill.Verbose($"Processing unban notification for ban {data}");

        var unban = await _db.GetServerUnbanAsync(data.UnbanId);
        if (unban == null)
        {
            _sawmill.Warning($"Unban in notification ({data.UnbanId}) didn't exist?");
            return;
        }
        var ban = await _db.GetServerBanAsync(unban.BanId);
        if (ban == null)
        {
            _sawmill.Warning($"Ban ({unban.BanId}) in notification of unban ({data.UnbanId}) didn't exist?");
            return;
        }

        PostUnbanNotificationMessage(unban, ban);
    }

    private bool CheckBanRateLimit()
    {
        lock (_banNotificationRateLimitStateLock)
        {
            var now = _gameTiming.RealTime;
            if (_banNotificationRateLimitStart + BanNotificationRateLimitTime < now)
            {
                // Rate limit period expired, restart it.
                _banNotificationRateLimitCount = 1;
                _banNotificationRateLimitStart = now;
                return true;
            }

            _banNotificationRateLimitCount += 1;
            return _banNotificationRateLimitCount <= BanNotificationRateLimitCount;
        }
    }

    private bool CheckUnbanRateLimit()
    {
        lock (_unbanNotificationRateLimitStateLock)
        {
            var now = _gameTiming.RealTime;
            if (_unbanNotificationRateLimitStart + UnbanNotificationRateLimitTime < now)
            {
                // Rate limit period expired, restart it.
                _unbanNotificationRateLimitCount = 1;
                _unbanNotificationRateLimitStart = now;
                return true;
            }

            _unbanNotificationRateLimitCount += 1;
            return _unbanNotificationRateLimitCount <= UnbanNotificationRateLimitCount;
        }
    }

    /// <summary>
    /// Data sent along the notification channel for a single ban notification.
    /// </summary>
    private sealed class BanNotificationData
    {
        /// <summary>
        /// The ID of the new ban object in the database to check.
        /// </summary>
        [JsonRequired, JsonPropertyName("ban_id")]
        public int BanId { get; init; }

        /// <summary>
        /// The id of the server the ban was made on.
        /// This is used to avoid double work checking the ban on the originating server.
        /// </summary>
        /// <remarks>
        /// This is optional in case the ban was made outside a server (SS14.Admin)
        /// </remarks>
        [JsonPropertyName("server_id")]
        public int? ServerId { get; init; }
    }

    private sealed class UnbanNotificationData
    {
        /// <summary>
        /// The ID of the new unban object in the database to check.
        /// </summary>
        [JsonRequired, JsonPropertyName("unban_id")]
        public int UnbanId { get; init; }
    }

    private async void OnWebhookChanged(string url)
    {
        _webhookUrl = url;

        if (url == string.Empty)
            return;

        var match = DiscordRegex().Match(url);

        if (!match.Success)
        {
            // TODO: Ideally, CVar validation during setting should be better integrated
            _sawmill.Warning("Webhook URL does not appear to be valid. Using anyways...");
            await GetWebhookData(url);
            return;
        }

        if (match.Groups.Count <= 2)
        {
            _sawmill.Error("Could not get webhook ID or token.");
            return;
        }

        await GetWebhookData(url);
    }

    private async Task<WebhookData?> GetWebhookData(string url)
    {
        var response = await _httpClient.GetAsync(url);

        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _sawmill.Log(LogLevel.Error,
                $"Webhook returned bad status code when trying to get webhook data (perhaps the webhook URL is invalid?): {response.StatusCode}\nResponse: {content}");
            return null;
        }

        return JsonSerializer.Deserialize<WebhookData>(content);
    }

    private async void PostBanNotificationMessage(ServerBanDef banDef)
    {
        if (_webhookUrl.Length == 0)
            return;

        var match = DiscordRegex().Match(_webhookUrl);
        if (!match.Success)
            return;

        var adminName = "АДМИН";
        if (banDef.BanningAdmin != null)
        {
            var admin = await _locator.LookupIdAsync(banDef.BanningAdmin.Value);
            if (admin != null)
                adminName = admin.Username;
        }

        var playerName = "ИГРОК";
        if (banDef.UserId != null)
        {
            var player = await _locator.LookupIdAsync(banDef.UserId.Value);
            if (player != null)
                playerName = player.Username;
        }

        var payload = GeneratePayload(banDef.Reason, playerName, adminName, true);

        var request = await _httpClient.PostAsync($"{_webhookUrl}?wait=true",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        var content = await request.Content.ReadAsStringAsync();
        if (!request.IsSuccessStatusCode)
        {
            _sawmill.Log(LogLevel.Error,
                $"Discord returned bad status code when posting message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
        }
    }

    private async void PostUnbanNotificationMessage(ServerUnbanDef unbanDef, ServerBanDef banDef)
    {
        if (_webhookUrl.Length == 0)
            return;

        var match = DiscordRegex().Match(_webhookUrl);
        if (!match.Success)
            return;

        var adminName = "АДМИН";
        if (unbanDef.UnbanningAdmin != null)
        {
            var admin = await _locator.LookupIdAsync(unbanDef.UnbanningAdmin.Value);
            if (admin != null)
                adminName = admin.Username;
        }

        var playerName = "ИГРОК";
        if (banDef.UserId != null)
        {
            var player = await _locator.LookupIdAsync(banDef.UserId.Value);
            if (player != null)
                playerName = player.Username;
        }

        var payload = GeneratePayload(banDef.Reason, playerName, adminName, false);

        var request = await _httpClient.PostAsync($"{_webhookUrl}?wait=true",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

        var content = await request.Content.ReadAsStringAsync();
        if (!request.IsSuccessStatusCode)
        {
            _sawmill.Log(LogLevel.Error,
                $"Discord returned bad status code when posting message (perhaps the message is too long?): {request.StatusCode}\nResponse: {content}");
        }
    }

    private WebhookPayload GeneratePayload(string reason, string username, string admin, bool isBan)
        {
            var color = isBan ? 0xFF0000 : 0x41F097;

            var round = _gameTicker.RunLevel switch
            {
                GameRunLevel.PreRoundLobby => _gameTicker.RoundId == 0
                    ? "pre-round lobby after server restart" // first round after server restart has ID == 0
                    : $"pre-round lobby for round {_gameTicker.RoundId + 1}",
                GameRunLevel.InRound => $"round {_gameTicker.RoundId}",
                GameRunLevel.PostRound => $"post-round {_gameTicker.RoundId}",
                _ => throw new ArgumentOutOfRangeException(nameof(_gameTicker.RunLevel),
                    $"{_gameTicker.RunLevel} was not matched."),
            };

            return new WebhookPayload
            {
                Username = admin,
                Embeds = new List<WebhookEmbed>
                {
                    new()
                    {
                        Title = username,
                        Description = reason,
                        Color = color,
                        Footer = new WebhookEmbedFooter
                        {
                            Text = $"\ud83d\udc51 Europa+ ({round})",
                        },
                    },
                },
            };
        }
}

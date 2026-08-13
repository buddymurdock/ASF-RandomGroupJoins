using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomGroupJoins;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks an arbitrary bot/group/order, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomGroupJoins : IASF, IGitHubPluginUpdates {
	private const byte DefaultMinGroups = 1;
	private const byte DefaultMaxGroups = 3;
	private const ushort DefaultDelayBetweenJoinsInSeconds = 300;
	private const string BundledGroupsFileName = "groups.json";

	// Random per-bot target count of (our pool's) groups to be a member of, picked once and reused for the lifetime of the process
	private readonly ConcurrentDictionary<string, int> BotGroupTargets = new(StringComparer.Ordinal);

	private CancellationTokenSource? BackgroundLoopCts;
	private volatile bool CapacityWarningLogged;
	private ushort DelayBetweenJoinsInSeconds = DefaultDelayBetweenJoinsInSeconds;
	private bool Enabled;
	private volatile bool EmptyPoolWarningLogged;
	private ulong[] GroupIDs = [];
	private byte MaxGroups = DefaultMaxGroups;
	private byte MinGroups = DefaultMinGroups;
	private bool UseBundledGroups;

	public string Name => nameof(RandomGroupJoins);
	public string RepositoryName => "buddymurdock/ASF-RandomGroupJoins";
	public Version Version => typeof(RandomGroupJoins).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomGroupJoinsEnabled / RandomGroupJoinsMinGroups / RandomGroupJoinsMaxGroups / RandomGroupJoinsDelayBetweenJoins / RandomGroupJoinsGroupIDs / RandomGroupJoinsUseBundledGroups from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		HashSet<ulong> parsedGroupIDs = [];

		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomGroupJoins)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomGroupJoins)}MinGroups" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte minGroups):
						MinGroups = minGroups;

						break;
					case $"{nameof(RandomGroupJoins)}MaxGroups" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte maxGroups):
						MaxGroups = maxGroups;

						break;
					case $"{nameof(RandomGroupJoins)}DelayBetweenJoins" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort delayBetweenJoins) && (delayBetweenJoins > 0):
						DelayBetweenJoinsInSeconds = delayBetweenJoins;

						break;
					case $"{nameof(RandomGroupJoins)}UseBundledGroups" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						UseBundledGroups = configValue.GetBoolean();

						break;
					case $"{nameof(RandomGroupJoins)}GroupIDs" when configValue.ValueKind == JsonValueKind.Array:
						AddParsedGroupIDs(configValue, parsedGroupIDs);

						break;
				}
			}
		}

		if (UseBundledGroups) {
			LoadBundledGroupIDs(parsedGroupIDs);
		}

		GroupIDs = [.. parsedGroupIDs];

		if (MinGroups > MaxGroups) {
			(MinGroups, MaxGroups) = (MaxGroups, MinGroups);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomGroupJoins)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, will keep every bot's membership in the configured {GroupIDs.Length} group(s) between {MinGroups} and {MaxGroups}, with {DelayBetweenJoinsInSeconds}s between joins.");

		if (BackgroundLoopCts != null) {
			// OnASFInit() should only ever be called once per process, this is just a safety net against a possible double start
			return Task.CompletedTask;
		}

		BackgroundLoopCts = new CancellationTokenSource();

		Utilities.InBackground(() => BackgroundLoopAsync(BackgroundLoopCts.Token), true);

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	private async Task BackgroundLoopAsync(CancellationToken cancellationToken) {
		using PeriodicTimer timer = new(TimeSpan.FromSeconds(DelayBetweenJoinsInSeconds));

		while (!cancellationToken.IsCancellationRequested) {
			bool shouldContinue;

			try {
				shouldContinue = await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (!shouldContinue) {
				break;
			}

			try {
				await TryJoinSingleGroupAsync().ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Sends at most one group-join per call, from a random bot that hasn't reached its target yet, into a random group from the pool it's not already related to
	private async Task TryJoinSingleGroupAsync() {
		if (GroupIDs.Length == 0) {
			if (!EmptyPoolWarningLogged) {
				EmptyPoolWarningLogged = true;

				ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomGroupJoins)}GroupIDs is empty, set it in ASF.json to a list of Steam group SteamID64s for this plugin to do anything.");
			}

			return;
		}

		IReadOnlyDictionary<string, Bot>? bots = Bot.BotsReadOnly;

		if (bots == null) {
			return;
		}

		if (!CapacityWarningLogged && (MinGroups > GroupIDs.Length)) {
			CapacityWarningLogged = true;

			ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomGroupJoins)}MinGroups ({MinGroups}) is higher than the configured group pool size ({GroupIDs.Length}); some bots may never reach their target.");
		}

		List<Bot> onlineBots = [.. bots.Values.Where(static bot => bot.IsConnectedAndLoggedOn).OrderBy(static _ => Random.Shared.Next())];

		foreach (Bot bot in onlineBots) {
			int target = BotGroupTargets.GetOrAdd(bot.BotName, _ => GetRandomTarget());

			int currentGroups = GroupIDs.Count(groupID => bot.SteamFriends.GetClanRelationship(new SteamID(groupID)) == EClanRelationship.Member);

			if (currentGroups >= target) {
				continue;
			}

			ulong[] candidates = [.. GroupIDs.Where(groupID => bot.SteamFriends.GetClanRelationship(new SteamID(groupID)) == EClanRelationship.None)];

			if (candidates.Length == 0) {
				continue;
			}

			ulong candidateGroupID = candidates[Random.Shared.Next(candidates.Length)];

			bool success = await bot.ArchiWebHandler.JoinGroup(candidateGroupID).ConfigureAwait(false);

			if (success) {
				bot.ArchiLogger.LogGenericInfo($"Joined group {candidateGroupID} ({currentGroups + 1}/{target}).");
			} else {
				bot.ArchiLogger.LogGenericWarning($"Failed to join group {candidateGroupID}.");
			}

			return;
		}
	}

	private int GetRandomTarget() {
		int min = Math.Min(MinGroups, GroupIDs.Length);
		int max = Math.Min(MaxGroups, GroupIDs.Length);

		return min == max ? min : Random.Shared.Next(min, max + 1);
	}

	private static void AddParsedGroupIDs(JsonElement array, HashSet<ulong> target) {
		foreach (JsonElement groupElement in array.EnumerateArray()) {
			ulong? groupID = groupElement.ValueKind switch {
				JsonValueKind.Number when groupElement.TryGetUInt64(out ulong numericID) => numericID,
				JsonValueKind.String when ulong.TryParse(groupElement.GetString(), out ulong stringID) => stringID,
				_ => null
			};

			if ((groupID is { } validGroupID) && (validGroupID != 0) && new SteamID(validGroupID).IsClanAccount) {
				target.Add(validGroupID);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid {nameof(RandomGroupJoins)}GroupIDs entry: {groupElement}.");
			}
		}
	}

	// Loads groups.json shipped alongside the plugin DLL, adding its entries on top of whatever came from ASF.json
	private static void LoadBundledGroupIDs(HashSet<ulong> target) {
		string? pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

		if (string.IsNullOrEmpty(pluginDirectory)) {
			ASF.ArchiLogger.LogGenericWarning($"Could not determine plugin directory, {nameof(RandomGroupJoins)}UseBundledGroups will have no effect.");

			return;
		}

		string filePath = Path.Combine(pluginDirectory, BundledGroupsFileName);

		if (!File.Exists(filePath)) {
			ASF.ArchiLogger.LogGenericWarning($"{BundledGroupsFileName} not found next to the plugin, {nameof(RandomGroupJoins)}UseBundledGroups will have no effect.");

			return;
		}

		List<BundledGroupEntry>? entries;

		try {
			entries = JsonSerializer.Deserialize<List<BundledGroupEntry>>(File.ReadAllText(filePath));
		} catch (JsonException e) {
			ASF.ArchiLogger.LogGenericException(e);

			return;
		}

		if (entries == null) {
			return;
		}

		foreach (BundledGroupEntry entry in entries) {
			if ((entry.Id != 0) && new SteamID(entry.Id).IsClanAccount) {
				target.Add(entry.Id);
			} else {
				ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid entry in {BundledGroupsFileName}: {entry.Id}.");
			}
		}
	}

	private sealed record BundledGroupEntry([property: JsonPropertyName("id")] ulong Id, [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("url")] string? Url);
}
#pragma warning restore CA5394 // Randomness here only picks an arbitrary bot/group/order, it's not used for anything security-sensitive
#pragma warning restore CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning restore CA1812 // ASF uses this class during runtime

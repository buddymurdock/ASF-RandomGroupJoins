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
	private const ushort DefaultMinDelayBetweenJoinsInSeconds = 180;
	private const ushort DefaultMaxDelayBetweenJoinsInSeconds = 420;
	private const string BundledGroupsFileName = "groups.json";

	// Random per-bot target count of (our pool's) groups to be a member of, picked once and reused for the lifetime of the process
	private readonly ConcurrentDictionary<string, int> BotGroupTargets = new(StringComparer.Ordinal);

	private CancellationTokenSource? BackgroundLoopCts;
	private volatile bool CapacityWarningLogged;
	private bool Enabled;
	private volatile bool EmptyPoolWarningLogged;
	private ulong[] GroupIDs = [];
	private ushort MaxDelayBetweenJoinsInSeconds = DefaultMaxDelayBetweenJoinsInSeconds;
	private byte MaxGroups = DefaultMaxGroups;
	private ushort MinDelayBetweenJoinsInSeconds = DefaultMinDelayBetweenJoinsInSeconds;
	private byte MinGroups = DefaultMinGroups;
	private bool UseBundledGroups;

	public string Name => nameof(RandomGroupJoins);
	public string RepositoryName => "buddymurdock/ASF-RandomGroupJoins";
	public Version Version => typeof(RandomGroupJoins).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomGroupJoinsEnabled / RandomGroupJoinsMinGroups / RandomGroupJoinsMaxGroups / RandomGroupJoinsMinDelayBetweenJoins / RandomGroupJoinsMaxDelayBetweenJoins /
	// RandomGroupJoinsGroupIDs / RandomGroupJoinsUseBundledGroups from the global ASF.json config
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
					case $"{nameof(RandomGroupJoins)}MinDelayBetweenJoins" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelayBetweenJoins) && (minDelayBetweenJoins > 0):
						MinDelayBetweenJoinsInSeconds = minDelayBetweenJoins;

						break;
					case $"{nameof(RandomGroupJoins)}MaxDelayBetweenJoins" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelayBetweenJoins) && (maxDelayBetweenJoins > 0):
						MaxDelayBetweenJoinsInSeconds = maxDelayBetweenJoins;

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

		if (MinDelayBetweenJoinsInSeconds > MaxDelayBetweenJoinsInSeconds) {
			(MinDelayBetweenJoinsInSeconds, MaxDelayBetweenJoinsInSeconds) = (MaxDelayBetweenJoinsInSeconds, MinDelayBetweenJoinsInSeconds);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomGroupJoins)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, will keep every bot's membership in the configured {GroupIDs.Length} group(s) between {MinGroups} and {MaxGroups}, with {MinDelayBetweenJoinsInSeconds}-{MaxDelayBetweenJoinsInSeconds}s between joins.");

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

	// Delay is re-rolled every tick within [MinDelayBetweenJoinsInSeconds; MaxDelayBetweenJoinsInSeconds] instead of using a fixed-period PeriodicTimer -
	// a perfectly metronomic tick interval running around the clock is itself a machine-detectable pattern, independent of anything visible to other users
	private async Task BackgroundLoopAsync(CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			TimeSpan delay = GetRandomDelay(MinDelayBetweenJoinsInSeconds, MaxDelayBetweenJoinsInSeconds);

			try {
				await LongDelayAsync(delay, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			try {
				await TryJoinSingleGroupAsync().ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Task.Delay's underlying timer caps out at ~49.7 days (uint.MaxValue-1 ms) - a delay past that
	// throws ArgumentOutOfRangeException synchronously, which would go unhandled here and crash the
	// entire ASF process via OnUnobservedTaskException (this exact bug hit RandomNickname/RandomProfileAvatar/
	// RandomProfileBackground in production). Chunking sidesteps the limit for arbitrarily long delays -
	// needed here now that GetRandomDelay below no longer guarantees an upper bound the way uniform did.
	private static async Task LongDelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
		TimeSpan chunk = TimeSpan.FromDays(1);

		while (delay > chunk) {
			await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
			delay -= chunk;
		}

		if (delay > TimeSpan.Zero) {
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	// Real people don't wait a uniformly random amount of time between actions - intervals tend
	// to cluster around a typical gap with occasional much shorter/longer ones (bursty/heavy-tailed),
	// not spread flat across [min, max]. Log-normal captures that: min/max become the ~5th/95th
	// percentiles rather than hard bounds, with sqrt(min*max) as the median.
	// z is clamped before use because extreme (min, max) ratios produce a large sigma - an un-clamped
	// Box-Muller tail can drive Math.Exp()/TimeSpan construction into Infinity/OverflowException, the
	// same failure class LongDelayAsync above was written to fix. The final Math.Clamp is a second,
	// independent safety net on the result itself, keeping delays (and LongDelayAsync's chunking loop)
	// bounded to something sane even for pathological configs.
	private static TimeSpan GetRandomDelay(ushort minSeconds, ushort maxSeconds) {
		if (minSeconds == maxSeconds) {
			return TimeSpan.FromSeconds(minSeconds);
		}

		double median = Math.Sqrt((double) minSeconds * maxSeconds);
		double sigma = Math.Log((double) maxSeconds / minSeconds) / (2 * 1.645);

		double u1 = 1.0 - Random.Shared.NextDouble();
		double u2 = Random.Shared.NextDouble();
		double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

		z = Math.Clamp(z, -3.5, 3.5);

		double seconds = median * Math.Exp(sigma * z);
		seconds = Math.Clamp(seconds, minSeconds / 10.0, maxSeconds * 5.0);

		return TimeSpan.FromSeconds(seconds);
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

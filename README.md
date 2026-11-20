# SS-7.0-States-Policy
shooting star bot

# Shooting Star cBot — Project Guide

This README is the maintenance map for the Shooting Star cBot. Use it to find the
primary file for a change, identify related files that may need coordinated
updates, and build the project.

## Project at a glance

The cBot is implemented as one `ShootingStar` class split across C# partial
classes. The partial files are not separate runtime components: they compile
together into the single robot declared in [ShootingStar.cs](./ShootingStar.cs).
The split is by responsibility so behavior can be located without searching one
large source file.

The project currently contains 47 hand-maintained `.cs` files. The SDK project
includes source files by default; generated files under `obj/` and `bin/` are
build output and should not be edited.

## Where to make a change

| If the change is about… | Start here | Also check |
|---|---|---|
| cTrader lifecycle, callback order, maintenance scheduling, or shutdown | [ShootingStar.cs](./ShootingStar.cs) | [Startup](./ShootingStar.Startup.cs), [Logging](./ShootingStar.Logging.cs) |
| Public cBot parameters or fixed implementation constants | [ShootingStar.Parameters.cs](./ShootingStar.Parameters.cs) | [StrategyConfiguration](./ShootingStar.StrategyConfiguration.cs), [ConfigurationProvenance](./ShootingStar.ConfigurationProvenance.cs), and the feature using the setting |
| Startup validation, parameter combinations, effective strategy settings, or ATR-derived values | [ShootingStar.StrategyConfiguration.cs](./ShootingStar.StrategyConfiguration.cs) | [Parameters](./ShootingStar.Parameters.cs), [Strategy](./ShootingStar.Strategy.cs) |
| Entry signal evaluation, movement gates, or candidate selection | [ShootingStar.EntryEvaluation.cs](./ShootingStar.EntryEvaluation.cs) | [MEV policy](./ShootingStar.MevPolicy.cs), [V2 model](./ShootingStar.V2Model.cs), [Policy decisions](./ShootingStar.PolicyDecision.cs), [Strategy order placement](./ShootingStar.StrategyOrderPlacement.cs) |
| Hourly/daily MEV calculations, regime scoring, or strategy selection | [ShootingStar.MevPolicy.cs](./ShootingStar.MevPolicy.cs) | [Parameters](./ShootingStar.Parameters.cs), [StrategyConfiguration](./ShootingStar.StrategyConfiguration.cs), [EntryEvaluation](./ShootingStar.EntryEvaluation.cs) |
| Deferred probabilistic/policy decisions, retries, or expiry | [ShootingStar.PolicyDecision.cs](./ShootingStar.PolicyDecision.cs) | [V2 persistence](./ShootingStar.V2PolicyPersistence.cs), [EntryEvaluation](./ShootingStar.EntryEvaluation.cs), [StrategyOrderPlacement](./ShootingStar.StrategyOrderPlacement.cs), [Parameters](./ShootingStar.Parameters.cs) |
| Creating a strategy order or reconciling an uncertain broker result | [ShootingStar.StrategyOrderPlacement.cs](./ShootingStar.StrategyOrderPlacement.cs) | [SafeSeparation](./ShootingStar.SafeSeparation.cs), [TradeEvents](./ShootingStar.TradeEvents.cs), [TradeIdeaTelemetry](./ShootingStar.TradeIdeaTelemetry.cs), [RolloverRestoration](./ShootingStar.RolloverRestoration.cs) |
| Pending-order trailing, reference barriers, or entry readiness | [ShootingStar.PendingOrderTrailing.cs](./ShootingStar.PendingOrderTrailing.cs) | [PendingOrderModification](./ShootingStar.PendingOrderModification.cs), [Reference](./ShootingStar.Reference.cs), [BrokerProtection](./ShootingStar.BrokerProtection.cs) |
| Applying a pending-order modification or registering a newly placed order | [ShootingStar.PendingOrderModification.cs](./ShootingStar.PendingOrderModification.cs) | [TradeManagement](./ShootingStar.TradeManagement.cs), [TradeEvents](./ShootingStar.TradeEvents.cs), [SafeSeparation](./ShootingStar.SafeSeparation.cs) |
| Pending-order risk cap, spacing, duplicate suppression, or provisional-order reconciliation | [ShootingStar.SafeSeparation.cs](./ShootingStar.SafeSeparation.cs) | [BrokerProtection](./ShootingStar.BrokerProtection.cs), [PendingOrderModification](./ShootingStar.PendingOrderModification.cs), [Parameters](./ShootingStar.Parameters.cs) |
| Position initialization or the normal position-management pass | [ShootingStar.PositionProtection.cs](./ShootingStar.PositionProtection.cs) | [PositionTrailing](./ShootingStar.PositionTrailing.cs), [TradeManagement](./ShootingStar.TradeManagement.cs) |
| Live position SL/TP trailing, ladder closes, or ladder-history reconciliation | [ShootingStar.PositionTrailing.cs](./ShootingStar.PositionTrailing.cs) | [PositionProtection](./ShootingStar.PositionProtection.cs), [TradeEvents](./ShootingStar.TradeEvents.cs), [TradeManagement](./ShootingStar.TradeManagement.cs) |
| Missing protection, retrying its restoration, or closing an unprotected position | [ShootingStar.PositionProtectionRecovery.cs](./ShootingStar.PositionProtectionRecovery.cs) | [PositionProtectionSafety](./ShootingStar.PositionProtectionSafety.cs), [BrokerProtection](./ShootingStar.BrokerProtection.cs) |
| Manual protection edits or unprotected-position safety actions | [ShootingStar.PositionProtectionSafety.cs](./ShootingStar.PositionProtectionSafety.cs) | [PositionProtection](./ShootingStar.PositionProtection.cs), [PositionProtectionRecovery](./ShootingStar.PositionProtectionRecovery.cs) |
| Broker minimum distances or validation of relative/absolute protection geometry | [ShootingStar.BrokerProtection.cs](./ShootingStar.BrokerProtection.cs) | [Infrastructure](./ShootingStar.Infrastructure.cs), [PendingOrderModification](./ShootingStar.PendingOrderModification.cs), [PositionProtectionRecovery](./ShootingStar.PositionProtectionRecovery.cs) |
| Market-session calendar, New York time conversion, or session overrides | [ShootingStar.MarketSession.cs](./ShootingStar.MarketSession.cs) | [MarketSessionRuntime](./ShootingStar.MarketSessionRuntime.cs), [MarketSessionDiagnostics](./ShootingStar.MarketSessionDiagnostics.cs), [Parameters](./ShootingStar.Parameters.cs) |
| Market-open status, executable quotes, or rollover-window calculations | [ShootingStar.MarketSessionRuntime.cs](./ShootingStar.MarketSessionRuntime.cs) | [MarketSession](./ShootingStar.MarketSession.cs), [Rollover](./ShootingStar.Rollover.cs), [Startup](./ShootingStar.Startup.cs) |
| Rollover phase transitions, flattening guard, or state-machine invariants | [ShootingStar.Rollover.cs](./ShootingStar.Rollover.cs) | [RolloverSnapshots](./ShootingStar.RolloverSnapshots.cs), [RolloverRestoration](./ShootingStar.RolloverRestoration.cs), [RolloverRecovery](./ShootingStar.RolloverRecovery.cs) |
| Which exposure is captured before rollover | [ShootingStar.RolloverSnapshots.cs](./ShootingStar.RolloverSnapshots.cs) | [StateModels](./ShootingStar.StateModels.cs), [RolloverRestoration](./ShootingStar.RolloverRestoration.cs) |
| Restoring positions/orders after rollover, including relative protection distances | [ShootingStar.RolloverRestoration.cs](./ShootingStar.RolloverRestoration.cs) | [RolloverSnapshots](./ShootingStar.RolloverSnapshots.cs), [RolloverRecovery](./ShootingStar.RolloverRecovery.cs), [BrokerProtection](./ShootingStar.BrokerProtection.cs) |
| Rollover cleanup, failed-snapshot finalization, or recovery completion | [ShootingStar.RolloverRecovery.cs](./ShootingStar.RolloverRecovery.cs) | [Rollover](./ShootingStar.Rollover.cs), [TradeStatistics](./ShootingStar.TradeStatistics.cs) |
| Startup history loading, deferred initialization, or readiness barriers | [ShootingStar.Startup.cs](./ShootingStar.Startup.cs) | [ShootingStar.cs](./ShootingStar.cs), [Reference](./ShootingStar.Reference.cs), [StrategyConfiguration](./ShootingStar.StrategyConfiguration.cs) |
| Reference-price history, rolling high/low range, or movement reference | [ShootingStar.Reference.cs](./ShootingStar.Reference.cs) | [PendingOrderTrailing](./ShootingStar.PendingOrderTrailing.cs), [EntryEvaluation](./ShootingStar.EntryEvaluation.cs), [Startup](./ShootingStar.Startup.cs) |
| Daily equity observations or day-boundary summaries | [ShootingStar.DailyEquity.cs](./ShootingStar.DailyEquity.cs) | [ChartTelemetry](./ShootingStar.ChartTelemetry.cs), [ShootingStar.cs](./ShootingStar.cs) |
| Closed-position accounting or trade-statistics summary | [ShootingStar.TradeStatistics.cs](./ShootingStar.TradeStatistics.cs) | [ClosingDealTelemetry](./ShootingStar.ClosingDealTelemetry.cs), [TradeIdeaSummary](./ShootingStar.TradeIdeaSummary.cs), [TradeEvents](./ShootingStar.TradeEvents.cs) |
| Trade identity, order/position attribution, or persisted lineage comments | [TradeIdentification](./ShootingStar.TradeIdentification.cs), [TradeIdeaTelemetry](./ShootingStar.TradeIdeaTelemetry.cs) | [CommentRecovery](./ShootingStar.CommentRecovery.cs), [TradeEvents](./ShootingStar.TradeEvents.cs), [TradeIdeaSummary](./ShootingStar.TradeIdeaSummary.cs) |
| Log format/levels, callback diagnostics, or startup/shutdown log summaries | [ShootingStar.Logging.cs](./ShootingStar.Logging.cs) | [VerboseSummaries](./ShootingStar.VerboseSummaries.cs), [ConfigurationProvenance](./ShootingStar.ConfigurationProvenance.cs) |
| On-chart controls or display layout | [ShootingStar.UserInterface.cs](./ShootingStar.UserInterface.cs) | [ChartTelemetry](./ShootingStar.ChartTelemetry.cs), [ShootingStar.cs](./ShootingStar.cs) |
| Optimization fitness calculation | [ShootingStar.OptimisationFitness.cs](./ShootingStar.OptimisationFitness.cs) | [TradeStatistics](./ShootingStar.TradeStatistics.cs), [TradeIdeaSummary](./ShootingStar.TradeIdeaSummary.cs) |

## Source-file reference

### Robot lifecycle, configuration, and shared foundations

| File | Responsibility |
|---|---|
| [ShootingStar.cs](./ShootingStar.cs) | Declares the `Robot` partial class, cTrader lifecycle callbacks, tick/timer orchestration, and the order of scheduled position management, pending-order maintenance, and entry evaluation. Start here for callback flow, not for feature-specific rules. |
| [ShootingStar.Startup.cs](./ShootingStar.Startup.cs) | Deferred startup phases: acquiring bars, loading history, settling, initializing indicators/strategy, and applying pre-ready exposure safety. |
| [ShootingStar.Parameters.cs](./ShootingStar.Parameters.cs) | cTrader-exposed parameters, shared parameter-derived values, and fixed constants such as retry and timer settings. |
| [ShootingStar.StrategyConfiguration.cs](./ShootingStar.StrategyConfiguration.cs) | Validates parameters and combinations, creates effective strategy settings, and validates/updates ATR-adjusted values. |
| [ShootingStar.ConfigurationProvenance.cs](./ShootingStar.ConfigurationProvenance.cs) | Builds a canonical configuration record, logs provenance, and records configuration changes/shutdown provenance. The schema-4 identity has a V2-specific canonical configuration that excludes legacy policy inputs. |
| [ShootingStar.StateModels.cs](./ShootingStar.StateModels.cs) | Shared enums, nested state objects, snapshots, and evidence models used by the partial classes. Update this when a shared model’s shape or semantics change. |
| [ShootingStar.Infrastructure.cs](./ShootingStar.Infrastructure.cs) | Common price, pip, and volume normalization utilities. Keep safety-sensitive rounding behavior consistent with its callers. |
| [ShootingStar.Logging.cs](./ShootingStar.Logging.cs) | Central logging APIs, levels, callback-failure handling, and startup/shutdown summaries. |
| [ShootingStar.TradeIdentification.cs](./ShootingStar.TradeIdentification.cs) | Maps labels to strategies and identifies this cBot’s positions and pending orders. |

### Signals, strategy selection, and order placement

| File | Responsibility |
|---|---|
| [ShootingStar.MevPolicy.cs](./ShootingStar.MevPolicy.cs) | Calculates hourly/daily Momentum Efficiency Values from current and still-forming bars, refreshes them at most once per second, and updates the V2 committed-state model or active rule-mapped policy diagnostics. |
| [ShootingStar.V2Model.cs](./ShootingStar.V2Model.cs) | Implements hourly/daily committed MEV states, the 9-regime map, regime confidence, extreme latches, V2 Boss probabilities, deterministic HMAC draws, and Stop/Limit geometry. |
| [ShootingStar.V2PolicyPersistence.cs](./ShootingStar.V2PolicyPersistence.cs) | Writes create-once per-hour reservations and integrity-checked, double-buffered decision journal records to instance LocalStorage. |
| [ShootingStar.EntryEvaluation.cs](./ShootingStar.EntryEvaluation.cs) | Evaluates entry signals, movement thresholds, rule-mapped candidates, and entry-pass diagnostics. |
| [ShootingStar.PolicyDecision.cs](./ShootingStar.PolicyDecision.cs) | Samples seeded Boss direction/Hold outcomes and conditional Wife strategy choices; processes deferred decisions, retry scheduling, expiry, and consumption of policy bars. |
| [ShootingStar.StrategyOrderPlacement.cs](./ShootingStar.StrategyOrderPlacement.cs) | Converts a selected setup into an order request, applies final placement safeguards, and classifies or reconciles ambiguous broker results. |
| [ShootingStar.Strategy.cs](./ShootingStar.Strategy.cs) | ATR state, hourly volatility factors, directional allocation, volume adjustment, and reusable trade/order snapshot helpers. |
| [ShootingStar.SafeSeparation.cs](./ShootingStar.SafeSeparation.cs) | Pending-order risk calculation/capping, safe-distance checks, rejected-modification suppression, and provisional order reconciliation. |
| [ShootingStar.BrokerProtection.cs](./ShootingStar.BrokerProtection.cs) | Broker metadata and minimum-distance rules; checks position protection, pending protection, and target geometry. |

### Pending orders and positions

| File | Responsibility |
|---|---|
| [ShootingStar.PendingOrderTrailing.cs](./ShootingStar.PendingOrderTrailing.cs) | Reference display and entry barrier checks; trailing of bot pending orders and strategy-specific target updates. |
| [ShootingStar.PendingOrderModification.cs](./ShootingStar.PendingOrderModification.cs) | Sends pending-order modifications/placements, validates broker-ready proposals, and updates trailing state only after broker-confirmed outcomes. |
| [ShootingStar.PositionProtection.cs](./ShootingStar.PositionProtection.cs) | Initializes live position state, handles position-modified events, and runs normal position management. |
| [ShootingStar.PositionTrailing.cs](./ShootingStar.PositionTrailing.cs) | Trails live stop-loss/take-profit protection and manages/reconciles position ladder actions. |
| [ShootingStar.PositionProtectionRecovery.cs](./ShootingStar.PositionProtectionRecovery.cs) | Tracks and retries recovery of missing protection on live positions. |
| [ShootingStar.PositionProtectionSafety.cs](./ShootingStar.PositionProtectionSafety.cs) | Safety close behavior for unprotected positions and reconciliation of manual/external protection changes. |
| [ShootingStar.TradeManagement.cs](./ShootingStar.TradeManagement.cs) | Persistent and in-memory trade-management state, including pending-order cancellation retries, circuit-breaker state, scale-up state, and ladder persistence helpers. |
| [ShootingStar.TradeEvents.cs](./ShootingStar.TradeEvents.cs) | cTrader position/order event callbacks, event-specific bookkeeping, and directional allocation handling. |
| [ShootingStar.RiskConversionRecovery.cs](./ShootingStar.RiskConversionRecovery.cs) | Recovery and terminal safety behavior when account-currency risk conversion cannot be completed reliably. |

### Market sessions, reference data, and rollover

| File | Responsibility |
|---|---|
| [ShootingStar.MarketSession.cs](./ShootingStar.MarketSession.cs) | New York time-zone initialization, market-session override parsing, and override validation. |
| [ShootingStar.MarketSessionRuntime.cs](./ShootingStar.MarketSessionRuntime.cs) | Converts local trading sessions to UTC and answers session-open, executable-quote, and rollover-window queries. |
| [ShootingStar.MarketSessionDiagnostics.cs](./ShootingStar.MarketSessionDiagnostics.cs) | Logs current session/configuration and runs deterministic startup assertions for normal and exceptional calendar boundaries. |
| [ShootingStar.Reference.cs](./ShootingStar.Reference.cs) | Loads and maintains tick/minute reference history, computes the rolling reference range, and supplies movement-reference prices. |
| [ShootingStar.Rollover.cs](./ShootingStar.Rollover.cs) | Owns rollover phase transitions, the broker rollover guard, runtime invariants, and startup self-tests for the state machine and restoration policy. |
| [ShootingStar.RolloverSnapshots.cs](./ShootingStar.RolloverSnapshots.cs) | Captures positions and pending orders for rollover and tracks their recovery identity/state. |
| [ShootingStar.RolloverRestoration.cs](./ShootingStar.RolloverRestoration.cs) | Restores captured positions and pending orders, schedules eligible retries, and applies restoration protection distances. |
| [ShootingStar.RolloverRecovery.cs](./ShootingStar.RolloverRecovery.cs) | Cleans up unsafe/unexpected exposure after a recovery failure, resolves snapshots, and finalizes controlled recovery. |

### Attribution, reporting, and telemetry

| File | Responsibility |
|---|---|
| [ShootingStar.PersistentCapsule.cs](./ShootingStar.PersistentCapsule.cs) | Encodes, validates, and recovers the versioned frozen-trade capsule stored in broker comments. |
| [ShootingStar.CommentRecovery.cs](./ShootingStar.CommentRecovery.cs) | Encodes/verifies lineage comments and recovers trade identity from comments on startup. |
| [ShootingStar.TradeIdeaTelemetry.cs](./ShootingStar.TradeIdeaTelemetry.cs) | Tracks submission correlation, entry origin, evidence, position/order association, fills, and attribution. |
| [ShootingStar.TradeIdeaSummary.cs](./ShootingStar.TradeIdeaSummary.cs) | Builds and logs logical trade-idea summaries across orders, fills, close events, and rollover segments. |
| [ShootingStar.ClosingDealTelemetry.cs](./ShootingStar.ClosingDealTelemetry.cs) | Observes closing-deal history for positions, including asynchronous/incomplete-history observations and shutdown handling. |
| [ShootingStar.TradeStatistics.cs](./ShootingStar.TradeStatistics.cs) | Closed-position accounting, logical-trade finalization, and aggregate trade-statistics summaries. |
| [ShootingStar.DailyEquity.cs](./ShootingStar.DailyEquity.cs) | Samples and aggregates equity by daily period and emits completed/partial daily summaries. |
| [ShootingStar.ChartTelemetry.cs](./ShootingStar.ChartTelemetry.cs) | Chart-oriented equity, MEV, and reference-range telemetry/statistics. |
| [ShootingStar.ModificationTelemetry.cs](./ShootingStar.ModificationTelemetry.cs) | Aggregates order and position modification observations and flushes periodic/final reports. |
| [ShootingStar.VerboseSummaries.cs](./ShootingStar.VerboseSummaries.cs) | Aggregates verbose entry-pass outcomes, no-entry decisions, reference updates, entry skips, and Daughter context candidates into bounded periodic summaries. |
| [ShootingStar.OptimisationFitness.cs](./ShootingStar.OptimisationFitness.cs) | Computes optimization fitness from the cBot’s trading results and logical-trade information. |
| [ShootingStar.UserInterface.cs](./ShootingStar.UserInterface.cs) | Creates and coordinates runtime strategy controls, settings panels, and their visibility. |

## Typical change paths

### Changing a cBot parameter

1. Add or edit the public cTrader parameter in
   [ShootingStar.Parameters.cs](./ShootingStar.Parameters.cs).
2. Validate its range and interactions in
   [ShootingStar.StrategyConfiguration.cs](./ShootingStar.StrategyConfiguration.cs).
3. Use it in the feature’s primary file from the tables above.
4. If it affects configuration identity or diagnostics, update
   [ShootingStar.ConfigurationProvenance.cs](./ShootingStar.ConfigurationProvenance.cs).
5. Preserve parameter names/types where possible; saved cTrader configurations
   may depend on them.

### Changing a trading or safety behavior

Find the feature’s primary file, then trace its callers and state:

1. Entry evaluation usually flows from `ShootingStar.cs` to
   `EntryEvaluation.cs`, optionally through `MevPolicy.cs` or
   `PolicyDecision.cs`, and then to `StrategyOrderPlacement.cs`.
2. Pending orders generally flow through `PendingOrderTrailing.cs`,
   `PendingOrderModification.cs`, and state/event reconciliation.
3. Live positions generally flow through `PositionProtection.cs`,
   `PositionTrailing.cs`, and the broker/safety recovery files.
4. Rollover changes usually span snapshot capture, restoration, and recovery;
   update the relevant self-tests in `Rollover.cs` when policy invariants change.

Do not modify only a diagnostic or summary file when the requested change is to
trading behavior. Conversely, keep observational telemetry changes out of the
execution path wherever possible.

### Adding shared state

Place state shared by multiple partials in
[ShootingStar.StateModels.cs](./ShootingStar.StateModels.cs), and keep ownership
and lifecycle updates with the feature that owns that state. Before introducing
a second copy of existing state, search for the current model and its
initialization, reset, persistence, and cleanup paths.

## Runtime flow and design notes

- `ShootingStar.cs` owns callback orchestration. Tick processing handles
  startup/rollover barriers and scheduled trading maintenance; the maintenance
  pass manages open positions, trails pending orders, then evaluates entries.
- Hourly and daily MEV use the latest bar in their respective series, including
  the still-forming live bar. Runtime refreshes are throttled to at most once
  per second; daily MEV is updated in probabilistic-auto mode.
- Timer processing supports safety/recovery and deferred work even when ticks
  are unavailable, such as during a market closure.
- Startup is staged in `Startup.cs`; strategy entries are gated until required
  history, indicators, configuration, and market readiness are available.
- The rollover state machine is safety-sensitive. Snapshot capture,
  restoration, and cleanup are separate responsibilities; a failed restore
  must not bypass cleanup or the runtime invariant checks.
- New pending orders store a versioned frozen-trade capsule after the existing
  `USI1` lineage ID in the broker comment:
  `USI1<32-character-guid>~<base64url-payload>`. The current payload is 44
  bytes, and the complete comment is 96 ASCII characters. It includes the
  strategy/direction, symbol fingerprint, Fractal Factor, original target and
  movement reference, initial protection distances, a compact
  single-precision volume value, and strategy percentages. The broker's live
  order/position volume remains authoritative. A CRC-16 detects accidental
  corruption; it is not a cryptographic integrity check. Legacy lineage-only
  `USI1` comments remain parseable.
- Capsule recovery depends on the broker preserving the full comment on both
  pending orders and resulting positions. Confirm that round-trip and the
  broker's comment-length limit on a demo account before deployment. An
  invalid pending-order capsule causes the order to be cancelled; a position
  with invalid capsule data and missing protection is closed rather than
  protected using guessed settings. Capsules are not used to attribute
  potentially aggregated netted positions.
- Broker comments are immutable for this purpose, so they cannot record
  partial-close ladder progress. After a restart where that progress is
  ambiguous, ladder actions are disabled for the recovered position to avoid
  repeating a close. Rollover snapshots carry capsules only in process memory;
  they cannot recover the interval after flattening if the process stops
  before restoration.
- Logging and telemetry should not change order decisions. Preserve the
  existing error reporting and callback-failure policies when adding
  diagnostics.
- Many collections are live cTrader collections that mutate during broker
  operations. Follow the existing stable-snapshot iteration pattern rather
  than changing a collection while iterating it.

## V2 MEV-state probabilistic policy

Probabilistic Auto uses committed hourly and daily MEV trend states instead of
the removed Son/Daughter confirmation path. A state change requires a material MEV
change, two qualifying refreshes, the configured hysteresis, and the hourly
or daily persistence interval. The paired states map to nine regimes; Range
always resolves to Hold. The current MEV defaults are length 10, boundaries
`-0.35`/`+0.35`, and extreme threshold `0.90`.

A diagnostic-only reversal watch measures return deceleration normalized by
strictly positive Sigma21. New live-bar highs/lows are distinct evidence
revisions; activation requires the 0.50 threshold, two revisions, and 30
seconds. It clears at 0.25, pauses when Sigma21/evidence is invalid, and
cancels after a 30-second invalid-evidence grace period or at the next hourly
bar. It does not alter Boss probabilities or entry eligibility.

Boss makes at most one decision per hourly bar. It samples a direction or Hold
from the regime-based probability table using deterministic HMAC-SHA256 draws
derived from the configured seed, symbol, model, bar, and draw role. The
decision key and frozen configuration are reserved in LocalStorage before the
Boss draw; subsequent outcomes and entry geometry are written to a
double-buffered, integrity-checked journal. Retries reuse the frozen decision
and never draw again. LocalStorage errors, corrupt state, and an existing
reservation fail closed. Live Probabilistic Auto is disabled in cTrader Cloud;
the durable-storage guarantee is limited to Desktop/CLI live mode.

Wife is consulted only for an extreme countertrend decision. It selects a
mean-reversion Limit or Stop-confirmation entry, with a default Stop
probability of 0.50. Stop entry distance is the corresponding effective Limit
distance multiplied by the Stop Order Multiplier (default 1.50, range
1.10–3.00). Both order types use the shared risk, protection, safe-separation,
and broker-distance checks. For Stop entries, the configured stop-loss
distance is divided by the multiplier and the take-profit distance is
multiplied by it. These adjusted distances are frozen with the order and
preserved through fill and rollover handling. Stop targets remain frozen while
pending, and rollover restoration preserves Limit versus Stop and reapplies the
frozen Stop distance.

Legacy policy controls have been removed from the parameter UI. Existing saved
parameter sets should be reviewed after upgrading. This release
fails closed on a previously reserved bar after restart rather than redrawing
its decision; live
backtest/optimization mode does not promise durable restart recovery. Validate
execution behavior with cTrader backtests and a demo account before live
deployment.

## Build and verification

The project targets .NET 6 and references `cTrader.Automate`. Build from this
directory after dependencies have been restored:

```sh
dotnet build "Shooting Star.csproj" --no-restore
```

The build produces the robot assembly and `.algo` package. A successful build
checks compilation, not trading behavior. For changes to execution, risk,
session, protection, or rollover logic, also run the relevant cTrader
backtest(s) and inspect the resulting logs. Production self-checks for rollover
and market-session invariants run during startup; keep them deterministic and
update them when their corresponding policy changes.

`BUILD_README.txt` contains the package/build note retained with this project.
This README is the source-organization and maintenance guide.

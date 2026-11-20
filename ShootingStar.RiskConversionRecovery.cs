using System;
using System.Globalization;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class ShootingStar
    {
        private const int RiskConversionRetryMinutes = 5;
        private const int RiskConversionRecoveryWindowMinutes = 120;
        private const int RiskConversionRecoverySchemaVersion = 1;

        private enum RiskConversionRecoveryState
        {
            Healthy,
            WaitingForRetry,
        }

        private RiskConversionRecoveryState _riskConversionRecoveryState =
            RiskConversionRecoveryState.Healthy;
        private DateTime? _nextRiskConversionRetryUtc;
        private bool _riskConversionEntriesBlocked;

        private void RegisterRiskConversionFailure(string failureCode, string failureDetail)
        {
            DateTime now = Server.TimeInUtc;
            if (_riskConversionRecoveryState == RiskConversionRecoveryState.Healthy)
            {
                CancelBotPendingOrdersForRiskConversion("risk-conversion-unavailable");
                LogError("RISK_CONVERSION", "CONVERSION_RECOVERY_STARTED",
                    "schemaVersion={0} startedUtc={1:O} nextRetryUtc={2:O} deadlineUtc={3:O} retryMinutes={4} windowMinutes={5} symbol={6} accountCurrency={7} failureCode={8} detail={9}",
                    RiskConversionRecoverySchemaVersion, now, now.AddMinutes(RiskConversionRetryMinutes),
                    now.AddMinutes(RiskConversionRecoveryWindowMinutes), RiskConversionRetryMinutes,
                    RiskConversionRecoveryWindowMinutes, Symbol.Name, Account.Asset.Name,
                    failureCode ?? "unknown", failureDetail ?? "none");
            }
            _riskConversionRecoveryState = RiskConversionRecoveryState.WaitingForRetry;
            _nextRiskConversionRetryUtc = now.AddMinutes(RiskConversionRetryMinutes);
            _riskConversionEntriesBlocked = true;
        }

        private void CancelBotPendingOrdersForRiskConversion(string reason)
        {
            CaptureBotPendingOrders(_pendingOrderTrailingSnapshot);
            foreach (PendingOrder order in _pendingOrderTrailingSnapshot)
                CancelPendingOrderWithRetry(order, reason);
        }

    }
}

using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    /// <summary>
    /// XAUUSD M15 EMA-trend pullback bot with Engulfing / Pin Bar confirmation.
    /// Entries are evaluated only on a new bar (OnBar); OnTick is not implemented.
    /// </summary>
    [Robot(TimeZone = TimeZones.Utc, AccessRights = AccessRights.None)]
    public class XauusdPullbackBot : Robot
    {
        #region Parameters

        [Parameter("Risk Percent", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 2.0, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Max Margin Usage", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 0.9, Group = "Risk")]
        public double MaxMarginUsage { get; set; }

        [Parameter("Max Positions", DefaultValue = 1, MinValue = 1, MaxValue = 10, Group = "Risk")]
        public int MaxPositions { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, MaxValue = 100, Group = "Indicators")]
        public int ATRPeriod { get; set; }

        [Parameter("Fast EMA", DefaultValue = 50, MinValue = 2, MaxValue = 500, Group = "Indicators")]
        public int FastEMA { get; set; }

        [Parameter("Slow EMA", DefaultValue = 200, MinValue = 3, MaxValue = 1000, Group = "Indicators")]
        public int SlowEMA { get; set; }

        [Parameter("Slope Lookback", DefaultValue = 3, MinValue = 1, MaxValue = 20, Group = "Indicators")]
        public int SlopeLookback { get; set; }

        [Parameter("ATR SL Multiplier", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 3.0, Group = "Stop Loss / Take Profit")]
        public double ATRMultiplier { get; set; }

        [Parameter("Min SL (ATR)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Group = "Stop Loss / Take Profit")]
        public double MinSlAtr { get; set; }

        [Parameter("Max SL (ATR)", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 10.0, Group = "Stop Loss / Take Profit")]
        public double MaxSlAtr { get; set; }

        [Parameter("Risk Reward Ratio", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 3.0, Group = "Stop Loss / Take Profit")]
        public double RRR { get; set; }

        [Parameter("Spread Buffer (price)", DefaultValue = 0.30, MinValue = 0.0, MaxValue = 5.0, Group = "Stop Loss / Take Profit")]
        public double SpreadBufferPrice { get; set; }

        [Parameter("Pin Wick Ratio", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0, Group = "Patterns")]
        public double PinWickRatio { get; set; }

        [Parameter("Pin Max Opposite Wick", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 3.0, Group = "Patterns")]
        public double PinMaxOppWick { get; set; }

        [Parameter("Pin Min Range (ATR)", DefaultValue = 0.8, MinValue = 0.1, MaxValue = 5.0, Group = "Patterns")]
        public double PinMinRangeAtr { get; set; }

        [Parameter("Pin Close Position", DefaultValue = 0.6, MinValue = 0.5, MaxValue = 1.0, Group = "Patterns")]
        public double PinClosePos { get; set; }

        [Parameter("Engulf Min Body (ATR)", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 5.0, Group = "Patterns")]
        public double EngMinBodyAtr { get; set; }

        [Parameter("Pullback (ATR)", DefaultValue = 0.25, MinValue = 0.0, MaxValue = 3.0, Group = "Patterns")]
        public double PullbackAtr { get; set; }

        [Parameter("Max Spread (price)", DefaultValue = 0.60, MinValue = 0.0, MaxValue = 10.0, Group = "Filters")]
        public double MaxSpreadPrice { get; set; }

        [Parameter("Use Session Filter", DefaultValue = true, Group = "Filters")]
        public bool UseSessionFilter { get; set; }

        [Parameter("Session Start Hour (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Filters")]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (UTC)", DefaultValue = 20, MinValue = 0, MaxValue = 23, Group = "Filters")]
        public int SessionEndHour { get; set; }

        [Parameter("Pip Size Override (0 = auto)", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 10.0, Group = "Execution")]
        public double PipSizeOverride { get; set; }

        [Parameter("Max Retries", DefaultValue = 2, MinValue = 0, MaxValue = 5, Group = "Execution")]
        public int MaxRetries { get; set; }

        [Parameter("Magic Label", DefaultValue = "XAU_PB_M15", Group = "Execution")]
        public string MagicLabel { get; set; }

        [Parameter("Min Bars", DefaultValue = 250, MinValue = 50, MaxValue = 5000, Group = "Execution")]
        public int MinBars { get; set; }

        #endregion

        #region Fields

        private enum SignalDir
        {
            None,
            Buy,
            Sell
        }

        private const double Eps = 1e-9;

        /// <summary>Hard floor for SL distance in pips, protects against broker minimum stop distance.</summary>
        private const double HardMinSlPips = 5.0;

        private ExponentialMovingAverage _emaFast;
        private ExponentialMovingAverage _emaSlow;
        private AverageTrueRange _atr;
        private DateTime _lastBarTime = DateTime.MinValue;

        private int _signalsSeen;
        private int _tradesOpened;
        private int _failedOrders;
        private int _skippedSl;
        private int _skippedVolume;

        #endregion

        #region Lifecycle

        /// <summary>Initializes indicators and prints symbol diagnostics.</summary>
        protected override void OnStart()
        {
            if (FastEMA >= SlowEMA)
            {
                Print("ERROR: FastEMA must be smaller than SlowEMA. Bot stopped.");
                Stop();
                return;
            }

            _emaFast = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastEMA);
            _emaSlow = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowEMA);
            _atr = Indicators.AverageTrueRange(ATRPeriod, MovingAverageType.Exponential);

            double ps = GetPipSize();
            Print("PipSize={0} PipValue={1} TickSize={2} TickValue={3}", Symbol.PipSize, Symbol.PipValue, Symbol.TickSize, Symbol.TickValue);
            Print("VolumeInUnitsMin={0} VolumeInUnitsMax={1} VolumeInUnitsStep={2}", Symbol.VolumeInUnitsMin, Symbol.VolumeInUnitsMax, Symbol.VolumeInUnitsStep);
            Print("Effective pip size used by bot = {0}", ps);

            if (Math.Abs(ps - 0.1) > Eps)
            {
                Print("WARNING: effective pip size is {0}, not 0.1. SL/TP in pips are computed from price distance / pip size, so risk stays consistent, but verify broker settings.", ps);
            }
        }

        /// <summary>Signal evaluation on each newly opened bar.</summary>
        protected override void OnBar()
        {
            if (Bars.Count < MinBars)
                return;

            DateTime barTime = Bars.OpenTimes.Last(0);
            if (barTime == _lastBarTime)
                return;
            _lastBarTime = barTime;

            if (!InSession(barTime))
                return;

            double spread = Symbol.Ask - Symbol.Bid;
            if (!IsValid(spread) || spread > MaxSpreadPrice + Eps)
                return;

            if (CountOpenPositions() >= MaxPositions)
                return;

            double atr1 = _atr.Result.Last(1);
            if (!IsValid(atr1) || atr1 <= Eps)
                return;

            SignalDir dir = GetDirection(atr1);
            if (dir == SignalDir.None)
                return;

            _signalsSeen++;
            TryExecute(dir, atr1);
        }

        /// <summary>Prints a run summary.</summary>
        protected override void OnStop()
        {
            Print("Summary: signals={0}, opened={1}, failedOrders={2}, skippedSL={3}, skippedVolume={4}", _signalsSeen, _tradesOpened, _failedOrders, _skippedSl, _skippedVolume);
        }

        #endregion

        #region Execution

        /// <summary>Calculates SL/TP/volume and sends the order, retrying on the same bar.</summary>
        private void TryExecute(SignalDir dir, double atr1)
        {
            double widen = 1.0;
            int maxAttempts = 1 + Math.Max(0, MaxRetries);

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (Bars.OpenTimes.Last(0) != _lastBarTime)
                    return;

                if (attempt > 0)
                {
                    double spreadNow = Symbol.Ask - Symbol.Bid;
                    if (!IsValid(spreadNow) || spreadNow > MaxSpreadPrice + Eps)
                        return;
                    if (CountOpenPositions() >= MaxPositions)
                        return;
                }

                double ps = GetPipSize();
                if (ps <= Eps)
                {
                    Print("Skip: invalid pip size.");
                    return;
                }

                double slD = CalcSlDistance(dir, atr1, widen);
                if (slD <= Eps)
                {
                    _skippedSl++;
                    Print("Skip: SL distance invalid or above MaxSlAtr.");
                    return;
                }

                double tpD = RRR * slD;
                double slPips = slD / ps;
                double tpPips = tpD / ps;
                double entry = dir == SignalDir.Buy ? Symbol.Ask : Symbol.Bid;

                double volume = CalcVolume(slD, slPips, entry);
                if (volume <= Eps)
                {
                    _skippedVolume++;
                    return;
                }

                TradeType tradeType = dir == SignalDir.Buy ? TradeType.Buy : TradeType.Sell;
                TradeResult result = ExecuteMarketOrder(tradeType, SymbolName, volume, MagicLabel, slPips, tpPips);

                if (result.IsSuccessful)
                {
                    _tradesOpened++;
                    Print("Order OK: {0} vol={1} slPips={2:F1} tpPips={3:F1} attempt={4}", tradeType, volume, slPips, tpPips, attempt + 1);
                    return;
                }

                _failedOrders++;
                string err = result.Error.ToString();
                Print("Order failed (attempt {0}/{1}): {2}", attempt + 1, maxAttempts, err);

                // If the broker rejected the stops (e.g. below minimum StopLevel), widen SL on the retry.
                if (err.IndexOf("Stop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    err.IndexOf("Sl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    err.IndexOf("Invalid", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    widen *= 1.25;
                    Print("Stops rejected, widening SL by factor {0:F2} for next attempt.", widen);
                }
            }
        }

        /// <summary>Returns SL distance in price units, or -1 if the trade must be skipped.</summary>
        private double CalcSlDistance(SignalDir dir, double atr1, double widen)
        {
            double ps = GetPipSize();
            double ask = Symbol.Ask;
            double bid = Symbol.Bid;
            if (!IsValid(ask) || !IsValid(bid) || !IsValid(atr1) || atr1 <= Eps || ps <= Eps)
                return -1.0;

            double high1 = Bars.HighPrices.Last(1);
            double low1 = Bars.LowPrices.Last(1);
            if (!IsValid(high1) || !IsValid(low1))
                return -1.0;

            double buf = SpreadBufferPrice + (ask - bid);
            double slAtr = ATRMultiplier * atr1;
            double slStruct = dir == SignalDir.Buy ? ask - (low1 - buf) : (high1 + buf) - bid;

            double slD = Math.Max(slAtr, slStruct);

            double minD = MinSlAtr * atr1;
            if (slD < minD)
                slD = minD;

            double hardMin = HardMinSlPips * ps;
            if (slD < hardMin)
                slD = hardMin;

            slD *= widen;

            if (!IsValid(slD) || slD > MaxSlAtr * atr1 + Eps)
                return -1.0;

            return slD;
        }

        /// <summary>Returns normalized volume in units, or 0 if the trade must be skipped.</summary>
        private double CalcVolume(double slD, double slPips, double entryPrice)
        {
            double riskMoney = Account.Equity * RiskPercent / 100.0;
            if (!IsValid(riskMoney) || riskMoney <= Eps || slPips <= Eps || slD <= Eps)
                return 0.0;

            double pipValue = Symbol.PipValue;
            double tickSize = Symbol.TickSize;
            double tickValue = Symbol.TickValue;
            if (!IsValid(pipValue) || pipValue <= Eps || !IsValid(tickSize) || tickSize <= Eps || !IsValid(tickValue) || tickValue <= Eps)
            {
                Print("Skip: invalid PipValue/TickSize/TickValue.");
                return 0.0;
            }

            double vRisk = riskMoney / (slPips * pipValue);
            double moneyPerUnitAtSl = slD * tickValue / tickSize;
            double vAlt = riskMoney / moneyPerUnitAtSl;

            if (!IsValid(vAlt) || vAlt <= Eps)
                return 0.0;

            if (Math.Abs(vRisk - vAlt) / vAlt > 0.02)
            {
                Print("WARNING: volume mismatch vRisk={0:F2} vs vAlt={1:F2}. Using vAlt.", vRisk, vAlt);
                vRisk = vAlt;
            }

            double leverage = Account.PreciseLeverage;
            if (!IsValid(leverage) || leverage <= Eps || !IsValid(entryPrice) || entryPrice <= Eps)
                return 0.0;

            double perUnit = entryPrice / leverage;
            double vMargin = Account.FreeMargin * MaxMarginUsage / perUnit;
            if (!IsValid(vMargin) || vMargin <= Eps)
            {
                Print("Skip: insufficient free margin.");
                return 0.0;
            }

            double v = Math.Min(vRisk, Math.Min(vMargin, Symbol.VolumeInUnitsMax));
            v = Symbol.NormalizeVolumeInUnits(v, RoundingMode.Down);

            if (!IsValid(v) || v < Symbol.VolumeInUnitsMin - Eps || v <= Eps)
            {
                Print("Skip: volume {0} below minimum {1}.", v, Symbol.VolumeInUnitsMin);
                return 0.0;
            }

            if (v * moneyPerUnitAtSl > 1.25 * riskMoney)
            {
                Print("Skip: normalized volume risks more than 125% of target risk.");
                return 0.0;
            }

            return v;
        }

        #endregion

        #region Signal logic

        /// <summary>Evaluates the trend-pullback-pattern rules on the last closed bars.</summary>
        private SignalDir GetDirection(double atr1)
        {
            double o1 = Bars.OpenPrices.Last(1);
            double h1 = Bars.HighPrices.Last(1);
            double l1 = Bars.LowPrices.Last(1);
            double c1 = Bars.ClosePrices.Last(1);
            double o2 = Bars.OpenPrices.Last(2);
            double c2 = Bars.ClosePrices.Last(2);

            double emaF1 = _emaFast.Result.Last(1);
            double emaS1 = _emaSlow.Result.Last(1);
            double emaFPast = _emaFast.Result.Last(1 + SlopeLookback);

            if (!IsValid(o1) || !IsValid(h1) || !IsValid(l1) || !IsValid(c1) ||
                !IsValid(o2) || !IsValid(c2) ||
                !IsValid(emaF1) || !IsValid(emaS1) || !IsValid(emaFPast))
                return SignalDir.None;

            bool buy = emaF1 > emaS1 + Eps
                       && c1 > emaS1 + Eps
                       && emaF1 > emaFPast + Eps
                       && l1 <= emaF1 + PullbackAtr * atr1 + Eps
                       && (IsBullEngulf(o1, c1, o2, c2, atr1) || IsBullPin(o1, h1, l1, c1, atr1));

            bool sell = emaF1 < emaS1 - Eps
                        && c1 < emaS1 - Eps
                        && emaF1 < emaFPast - Eps
                        && h1 >= emaF1 - PullbackAtr * atr1 - Eps
                        && (IsBearEngulf(o1, c1, o2, c2, atr1) || IsBearPin(o1, h1, l1, c1, atr1));

            if (buy && sell)
                return SignalDir.None;
            if (buy)
                return SignalDir.Buy;
            if (sell)
                return SignalDir.Sell;
            return SignalDir.None;
        }

        /// <summary>Bullish engulfing: bar 1 (last closed) engulfs bar 2.</summary>
        private bool IsBullEngulf(double o1, double c1, double o2, double c2, double atr1)
        {
            double body1 = Math.Abs(c1 - o1);
            return c1 > o1 + Eps
                   && c2 < o2 - Eps
                   && o1 <= c2 + Eps
                   && c1 >= o2 - Eps
                   && body1 >= EngMinBodyAtr * atr1 - Eps;
        }

        /// <summary>Bearish engulfing: bar 1 (last closed) engulfs bar 2.</summary>
        private bool IsBearEngulf(double o1, double c1, double o2, double c2, double atr1)
        {
            double body1 = Math.Abs(c1 - o1);
            return c1 < o1 - Eps
                   && c2 > o2 + Eps
                   && o1 >= c2 - Eps
                   && c1 <= o2 + Eps
                   && body1 >= EngMinBodyAtr * atr1 - Eps;
        }

        /// <summary>Bullish pin bar (long lower wick).</summary>
        private bool IsBullPin(double o1, double h1, double l1, double c1, double atr1)
        {
            double range = h1 - l1;
            if (range <= Eps)
                return false;

            double body = Math.Abs(c1 - o1);
            if (body < Eps)
                body = 0.1 * range;

            double upper = h1 - Math.Max(o1, c1);
            double lower = Math.Min(o1, c1) - l1;

            return lower >= PinWickRatio * body - Eps
                   && upper <= PinMaxOppWick * body + Eps
                   && range >= PinMinRangeAtr * atr1 - Eps
                   && c1 >= l1 + PinClosePos * range - Eps;
        }

        /// <summary>Bearish pin bar (long upper wick).</summary>
        private bool IsBearPin(double o1, double h1, double l1, double c1, double atr1)
        {
            double range = h1 - l1;
            if (range <= Eps)
                return false;

            double body = Math.Abs(c1 - o1);
            if (body < Eps)
                body = 0.1 * range;

            double upper = h1 - Math.Max(o1, c1);
            double lower = Math.Min(o1, c1) - l1;

            return upper >= PinWickRatio * body - Eps
                   && lower <= PinMaxOppWick * body + Eps
                   && range >= PinMinRangeAtr * atr1 - Eps
                   && c1 <= h1 - PinClosePos * range + Eps;
        }

        #endregion

        #region Helpers

        /// <summary>Counts open positions for this bot's label and symbol.</summary>
        private int CountOpenPositions()
        {
            Position[] found = Positions.FindAll(MagicLabel, SymbolName);
            return found == null ? 0 : found.Length;
        }

        /// <summary>True if the bar's UTC hour is inside the configured session (wraps past midnight if start &gt; end).</summary>
        private bool InSession(DateTime barTime)
        {
            if (!UseSessionFilter)
                return true;

            int hour = barTime.Hour;
            if (SessionStartHour == SessionEndHour)
                return true;
            if (SessionStartHour < SessionEndHour)
                return hour >= SessionStartHour && hour < SessionEndHour;
            return hour >= SessionStartHour || hour < SessionEndHour;
        }

        /// <summary>Effective pip size: override if set, otherwise Symbol.PipSize.</summary>
        private double GetPipSize()
        {
            return PipSizeOverride > Eps ? PipSizeOverride : Symbol.PipSize;
        }

        /// <summary>True if the value is a finite number.</summary>
        private static bool IsValid(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        #endregion
    }
}

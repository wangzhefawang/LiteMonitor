namespace LiteMonitor.src.SystemServices
{
    /// <summary>
    /// 边沿触发的周期门：按累计的真实秒数划分周期，每个周期内 Due 只返回一次 true。
    /// 与 "seconds % period == n" 不同：刷新间隔小于 1 秒时不会在同一秒内连续触发，
    /// 刷新间隔大于 1 秒时也不会因为秒数被跳过而漏掉。
    /// 非线程安全，由同一个调用线程每轮求值一次。
    /// </summary>
    internal sealed class IntervalGate
    {
        private readonly int _period;
        private readonly int _offset;
        private long _lastBucket = -1;

        /// <param name="period">周期（秒）</param>
        /// <param name="offset">错峰偏移（秒）：周期序号为 (seconds + offset) / period</param>
        public IntervalGate(int period, int offset = 0)
        {
            _period = period;
            _offset = offset;
        }

        /// <summary>
        /// 周期序号与上次触发时不同则返回 true。首次调用必然触发（上次序号初始为 -1）。
        /// </summary>
        public bool Due(long seconds)
        {
            long bucket = (seconds + _offset) / _period;
            if (bucket == _lastBucket) return false;
            _lastBucket = bucket;
            return true;
        }
    }
}

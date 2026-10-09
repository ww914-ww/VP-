using System;
using System.Threading;
using System.Threading.Tasks;

namespace MoveImageForm.Services
{
    /// <summary>
    /// 自动恢复搬运服务：解决用户停止搬运后忘记重新开启的问题。
    ///
    /// 语义（与需求逐条对应）：
    /// 1. 仅在账号处于登录在线状态时生效（isOnline/canResume 为真才计时）；
    /// 2. 检测到当前不在搬运状态时开始计时，连续满足条件达到设定间隔后自动开启搬运；
    ///    期间任意时刻恢复搬运 / 离线 / 关闭功能 → 计时清零，绝不在条件不满足时动作；
    /// 3. 当前已在搬运状态 → 跳过，不做任何操作；
    /// 4. 账号离线或退出登录 → 不触发；功能关闭 → 不执行任何自动操作。
    ///
    /// 所有状态判断通过委托注入，服务本身无 WPF 依赖，可单元测试。
    /// </summary>
    public class AutoResumeService
    {
        private readonly Func<AppConfig> _getConfig;
        private readonly Func<bool> _isOnline;    // 账号是否登录在线
        private readonly Func<bool> _isMoving;    // 当前是否正在搬运
        private readonly Func<bool> _canResume;   // 完整前置校验（在线+有效目录+上传权限，避免触发后弹窗）
        private readonly Action _startMoving;     // 由调用方封送 UI 线程执行 StartMoving
        private readonly Action<string> _log;
        private readonly Func<DateTime> _now;     // 时间源（测试可注入）
        private readonly TimeSpan _tickInterval;

        private CancellationTokenSource _cts;
        private DateTime? _idleSince;             // 进入"在线且未搬运"状态的起始时刻（条件不满足即清零）

        public AutoResumeService(Func<AppConfig> getConfig,
            Func<bool> isOnline, Func<bool> isMoving, Func<bool> canResume,
            Action startMoving, Action<string> log,
            Func<DateTime> now = null, TimeSpan? tickInterval = null)
        {
            _getConfig = getConfig;
            _isOnline = isOnline;
            _isMoving = isMoving;
            _canResume = canResume;
            _startMoving = startMoving;
            _log = log ?? (m => { });
            _now = now ?? (() => DateTime.Now);
            _tickInterval = tickInterval ?? TimeSpan.FromSeconds(2);
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            Task.Run(() => Loop(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        private async Task Loop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Evaluate();
                    await Task.Delay(_tickInterval, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log("[自动恢复] 服务循环异常终止: " + ex.Message);
            }
        }

        /// <summary>执行一次状态评估（循环驱动；测试可直接调用）</summary>
        public void Evaluate()
        {
            try
            {
                var config = _getConfig();

                // 功能关闭：不执行任何自动操作，计时清零
                if (config == null || !config.AutoResumeEnabled)
                {
                    _idleSince = null;
                    return;
                }

                // 已在搬运：跳过，不做任何操作（计时清零）
                if (_isMoving())
                {
                    _idleSince = null;
                    return;
                }

                // 账号离线/退出登录或前置条件不足：不触发，计时清零
                if (!_isOnline() || !_canResume())
                {
                    _idleSince = null;
                    return;
                }

                TimeSpan interval = config.GetAutoResumeInterval();
                DateTime now = _now();

                // 刚进入"在线且未搬运"状态：开始计时
                if (_idleSince == null)
                {
                    _idleSince = now;
                    return;
                }

                // 未达设定间隔：继续等待
                if (now - _idleSince.Value < interval)
                    return;

                // 到达间隔：自动开启搬运（StartMoving 内部仍有 _isRunning 守卫，双保险）
                _idleSince = null;
                _log($"[自动恢复] 账号在线且停止搬运已达 {FormatInterval(interval)}，自动开启搬运");
                _startMoving();
            }
            catch (Exception ex)
            {
                // 自动恢复是辅助功能，任何异常都不影响搬运业务与 UI
                _log("[自动恢复] 评估异常: " + ex.Message);
            }
        }

        private static string FormatInterval(TimeSpan interval)
        {
            if (interval.TotalHours >= 1) return $"{interval.TotalHours:0.#} 小时";
            if (interval.TotalMinutes >= 1) return $"{interval.TotalMinutes:0.#} 分钟";
            return $"{interval.TotalSeconds:0.#} 秒";
        }
    }
}

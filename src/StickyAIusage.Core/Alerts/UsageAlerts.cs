using System.Globalization;
using StickyAIusage.Services;

namespace StickyAIusage.Alerts;

/// <summary>
/// Runs the <see cref="AlertPlanner"/> on every refresh and keeps its state
/// in <c>cache/alerts.json</c>, so an alert given before a restart is not given
/// again after it. The file is read on first use (on the refresh's background
/// thread) and written only when the state changed. Every alert given or
/// dropped gets a log line. One refresh at a time; never throws.
/// </summary>
public sealed class UsageAlerts
{
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo? _zone;
    private readonly CultureInfo? _culture;
    private AlertState? _state;
    private string? _saved;
    private bool _saveFailureLogged;

    public UsageAlerts(string statePath, Func<DateTimeOffset> clock, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        _statePath = statePath;
        _clock = clock;
        _zone = zone;
        _culture = culture;
    }

    /// <summary>The alerts to show for this reading (none in quiet hours or while muted).</summary>
    public IReadOnlyList<Alert> Update(IReadOnlyList<AlertInput> inputs, AlertSettings settings,
        IReadOnlyList<ViewModels.SessionActivity>? sessions = null)
    {
        try
        {
            if (_state is null)
            {
                _state = AlertStateFile.Load(_statePath);
                _saved = AlertStateFile.Serialize(_state);
            }
            var plan = AlertPlanner.Plan(inputs, settings, _state, _clock(), _zone, _culture, sessions);
            _state = plan.State;
            foreach (var alert in plan.Alerts)
                DiagnosticLog.WriteInfo($"Alert: {alert.Title} - {alert.Body} [{alert.Key}]");
            foreach (var alert in plan.Dropped)
                DiagnosticLog.WriteInfo($"Alert not shown (quiet hours or muted): {alert.Title} [{alert.Key}]");
            Save();
            return plan.Alerts;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UsageAlerts.Update", ex);
            return [];
        }
    }

    private void Save()
    {
        var content = AlertStateFile.Serialize(_state!);
        if (content == _saved)
            return;
        try
        {
            AtomicFile.WriteAllText(_statePath, content);
            _saved = content;
            _saveFailureLogged = false;
        }
        catch (Exception ex)
        {
            // Kept in memory: this run still won't repeat anything.
            if (!_saveFailureLogged)
                DiagnosticLog.WriteError("UsageAlerts.Save", ex);
            _saveFailureLogged = true;
        }
    }
}

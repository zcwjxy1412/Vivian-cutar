using System.Collections.ObjectModel;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;

namespace VIvian.App;

/// <summary>A candidate task row. Selecting it records the user's plan; it does not make the task executable.</summary>
public sealed class TaskRow(EngineTaskDefinition task, bool enabled) : ObservableObject
{
    public string TaskId { get; } = task.Id;
    public string Title { get; } = task.Title;
    public string Status { get; } = task.Status switch
    {
        CapabilityStatus.Verified => "已验证",
        CapabilityStatus.Unsupported => "不支持",
        _ => "候选 · 未验证 · 不可执行",
    };
    public string Note { get; } = $"上游标识 {task.Id} · 证据级别 {task.Evidence} · {task.Note}";
    private bool _enabled = enabled;
    public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; Raise(); Changed?.Invoke(); } } }
    public event Action? Changed;
}

/// <summary>The task plan of one game: order, selection, validation and persistence.</summary>
public sealed class TaskPlanViewModel : ObservableObject
{
    public GameId Game { get; }
    public string Name => RunPresentation.GameName(Game) + " · " + EngineCatalog.For(Game).EngineName + " " + EngineCatalog.For(Game).PinnedVersion;
    public long Revision { get; private set; }
    public ObservableCollection<TaskRow> Rows { get; } = [];
    private TaskRow? _selected;
    public TaskRow? Selected { get => _selected; set => Set(ref _selected, value); }
    private string _validation = "";
    public string Validation { get => _validation; private set => Set(ref _validation, value); }
    private bool _dirty;
    public bool IsDirty { get => _dirty; private set => Set(ref _dirty, value); }

    public TaskPlanViewModel(TaskPlan plan)
    {
        Game = plan.Game;
        Load(plan);
    }

    public void Load(TaskPlan plan)
    {
        Revision = plan.Revision;
        Rows.Clear();
        var tasks = EngineCatalog.For(Game).Tasks;
        foreach (var entry in plan.Entries)
        {
            var row = new TaskRow(tasks.Single(t => t.Id == entry.TaskId), entry.Enabled);
            row.Changed += OnChanged;
            Rows.Add(row);
        }
        IsDirty = false;
        Validate();
    }

    public TaskPlan ToPlan() => new(Game, Rows.Select(r => new TaskPlanEntry(r.TaskId, r.Enabled)).ToList(), Revision);

    public void Move(int delta)
    {
        if (Selected is not { } row) return;
        var index = Rows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Rows.Count) return;
        Rows.Move(index, target);
        Selected = row;
        OnChanged();
    }

    private void OnChanged() { IsDirty = true; Validate(); }

    public void Validate()
    {
        var blockers = TaskPlanRules.ExecutionBlockers(ToPlan());
        Validation = blockers.Count == 0 ? "可以执行。" : "不能执行：" + string.Join(" ", blockers);
    }
}

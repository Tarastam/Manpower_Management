using System.Text.Json;
using ManpowerManagement.Models;

namespace ManpowerManagement.Services;

// One keyed attendance change that has not been copied to SQL Server yet. Everyone counts as present for the standard
// hours on a working day, so only exceptions (leave, absent, other hours, a transfer) are ever stored.
public class StagedCell
{
    public int EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    // The grid shift the cell was keyed on, or "Pregnant" for the Pregnant page.
    public string Shift { get; set; } = "";
    public int WorkshopId { get; set; }
    public decimal Hours { get; set; }
    public int LeaveType { get; set; }
    public decimal LeaveHours { get; set; }
    // The cell was put back to its default, so any stored entry is removed.
    public bool Revert { get; set; }
    public string Actor { get; set; } = "";
    public long Version { get; set; }
    public DateTime AtUtc { get; set; }
}

public class StagedManpower
{
    public int WorkshopId { get; set; }
    public string Shift { get; set; } = "";
    public DateOnly Date { get; set; }
    public int I1067 { get; set; }
    public int I841 { get; set; }
    public int I733 { get; set; }
    public string Actor { get; set; } = "";
    public long Version { get; set; }
    public DateTime AtUtc { get; set; }
}

// A remark on one employee's day. Empty text means the remark was cleared, so any stored one is deleted.
public class StagedNote
{
    public int EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public string Text { get; set; } = "";
    public string Actor { get; set; } = "";
    public long Version { get; set; }
    public DateTime AtUtc { get; set; }
}

public class StagingState
{
    public long NextVersion { get; set; } = 1;
    // The 07:00 / 19:00 slot that was last copied to SQL, and the last business day that has a daily summary.
    public DateTime? LastSlot { get; set; }
    public DateOnly? LastFinalizedDay { get; set; }
    public List<StagedCell> Cells { get; set; } = [];
    public List<StagedManpower> Manpower { get; set; } = [];
    public List<StagedNote> Notes { get; set; } = [];
}

public record StagingSnapshot(List<StagedCell> Cells, List<StagedManpower> Manpower, List<StagedNote> Notes)
{
    public bool IsEmpty => Cells.Count == 0 && Manpower.Count == 0 && Notes.Count == 0;
}

// Keeps attendance edits in a file next to the app instead of writing every edit to the shared SQL Server.
// AttendanceFlushWorker copies them to SQL at 07:00 and 19:00 and then removes them from here.
public class AttendanceStagingStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly object gate = new();
    readonly string path;
    readonly ILogger<AttendanceStagingStore> logger;
    readonly Dictionary<(int Employee, DateOnly Date, string Shift), StagedCell> cells = [];
    readonly Dictionary<(int Workshop, string Shift, DateOnly Date), StagedManpower> manpower = [];
    readonly Dictionary<(int Employee, DateOnly Date), StagedNote> notes = [];
    long nextVersion = 1;
    DateTime? lastSlot;
    DateOnly? lastFinalizedDay;

    public AttendanceStagingStore(IHostEnvironment environment, ILogger<AttendanceStagingStore> logger)
        : this(Path.Combine(environment.ContentRootPath, "App_Data", "attendance-staging.json"), logger) { }

    public AttendanceStagingStore(string path, ILogger<AttendanceStagingStore> logger)
    {
        this.path = path; this.logger = logger;
        Load();
    }

    public DateTime? LastSlot { get { lock (gate) return lastSlot; } }
    public DateOnly? LastFinalizedDay { get { lock (gate) return lastFinalizedDay; } }
    public int PendingCount { get { lock (gate) return cells.Count + manpower.Count + notes.Count; } }

    public void StageCells(IEnumerable<StagedCell> changes)
    {
        lock (gate)
        {
            foreach (var change in changes)
            {
                change.Version = nextVersion++; change.AtUtc = DateTime.UtcNow;
                cells[(change.EmployeeId, change.Date, change.Shift)] = change;
            }
            Persist();
        }
    }

    public void StageManpower(IEnumerable<StagedManpower> changes)
    {
        lock (gate)
        {
            foreach (var change in changes)
            {
                change.Version = nextVersion++; change.AtUtc = DateTime.UtcNow;
                manpower[(change.WorkshopId, change.Shift, change.Date)] = change;
            }
            Persist();
        }
    }

    public void StageNotes(IEnumerable<StagedNote> changes)
    {
        lock (gate)
        {
            foreach (var change in changes)
            {
                change.Version = nextVersion++; change.AtUtc = DateTime.UtcNow;
                notes[(change.EmployeeId, change.Date)] = change;
            }
            Persist();
        }
    }

    // Pending remarks for the grid to lay over what is stored in SQL (an empty text hides the stored remark).
    public List<StagedNote> PendingNotes(Func<StagedNote, bool> filter)
    {
        lock (gate) return notes.Values.Where(filter).Select(n => Copy(n)).ToList();
    }

    // Pending cells for the grid to lay over what is stored in SQL.
    public List<StagedCell> PendingCells(Func<StagedCell, bool> filter)
    {
        lock (gate) return cells.Values.Where(filter).Select(c => Copy(c)).ToList();
    }

    public List<StagedManpower> PendingManpower(Func<StagedManpower, bool> filter)
    {
        lock (gate) return manpower.Values.Where(filter).Select(m => Copy(m)).ToList();
    }

    // Stored entries as they will look once the pending changes are copied to SQL, so readers (Dashboard, New Hires)
    // are current without waiting for 07:00 / 19:00. A pending change replaces or removes the stored entry for the same
    // employee, day and shift; `include` limits which pending changes are added (e.g. the workshops being reported on).
    public List<AttendanceEntry> Overlay(IEnumerable<AttendanceEntry> stored, DateOnly from, DateOnly to, Func<StagedCell, bool>? include = null)
    {
        var pending = PendingCells(c => c.Date >= from && c.Date <= to);
        if (pending.Count == 0) return stored.ToList();
        var replaced = pending.Select(c => (c.EmployeeId, c.Date, c.Shift)).ToHashSet();
        var result = stored.Where(e => !replaced.Contains((e.EmployeeId, e.BusinessDate, e.Shift))).ToList();
        foreach (var c in pending.Where(c => !c.Revert && (include is null || include(c))))
            result.Add(new AttendanceEntry
            {
                EmployeeId = c.EmployeeId, BusinessDate = c.Date, Shift = c.Shift, WorkshopId = c.WorkshopId, WorkingHours = c.Hours,
                LeaveType = (LeaveType)c.LeaveType, LeaveHours = c.LeaveHours, IsConfirmed = true
            });
        return result;
    }

    public StagingSnapshot Snapshot()
    {
        lock (gate) return new StagingSnapshot(cells.Values.Select(c => Copy(c)).ToList(), manpower.Values.Select(m => Copy(m)).ToList(), notes.Values.Select(n => Copy(n)).ToList());
    }

    // Called after SQL accepted the snapshot. An item is only dropped when nobody changed it since the snapshot,
    // so an edit made during a flush stays and goes out with the next one.
    public void Remove(StagingSnapshot flushed)
    {
        lock (gate)
        {
            foreach (var c in flushed.Cells)
                if (cells.TryGetValue((c.EmployeeId, c.Date, c.Shift), out var current) && current.Version == c.Version)
                    cells.Remove((c.EmployeeId, c.Date, c.Shift));
            foreach (var m in flushed.Manpower)
                if (manpower.TryGetValue((m.WorkshopId, m.Shift, m.Date), out var current) && current.Version == m.Version)
                    manpower.Remove((m.WorkshopId, m.Shift, m.Date));
            foreach (var n in flushed.Notes)
                if (notes.TryGetValue((n.EmployeeId, n.Date), out var current) && current.Version == n.Version)
                    notes.Remove((n.EmployeeId, n.Date));
            Persist();
        }
    }

    public void MarkSlot(DateTime slot, DateOnly? finalizedDay)
    {
        lock (gate)
        {
            lastSlot = slot;
            if (finalizedDay is not null) lastFinalizedDay = finalizedDay;
            Persist();
        }
    }

    static StagedCell Copy(StagedCell c) => new()
    {
        EmployeeId = c.EmployeeId, Date = c.Date, Shift = c.Shift, WorkshopId = c.WorkshopId, Hours = c.Hours, LeaveType = c.LeaveType,
        LeaveHours = c.LeaveHours, Revert = c.Revert, Actor = c.Actor, Version = c.Version, AtUtc = c.AtUtc
    };

    static StagedManpower Copy(StagedManpower m) => new()
    {
        WorkshopId = m.WorkshopId, Shift = m.Shift, Date = m.Date, I1067 = m.I1067, I841 = m.I841, I733 = m.I733,
        Actor = m.Actor, Version = m.Version, AtUtc = m.AtUtc
    };

    static StagedNote Copy(StagedNote n) => new()
    {
        EmployeeId = n.EmployeeId, Date = n.Date, Text = n.Text, Actor = n.Actor, Version = n.Version, AtUtc = n.AtUtc
    };

    void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<StagingState>(File.ReadAllText(path), Json) ?? new StagingState();
            foreach (var c in state.Cells) cells[(c.EmployeeId, c.Date, c.Shift)] = c;
            foreach (var m in state.Manpower) manpower[(m.WorkshopId, m.Shift, m.Date)] = m;
            foreach (var n in state.Notes ?? []) notes[(n.EmployeeId, n.Date)] = n;
            nextVersion = Math.Max(state.NextVersion, 1); lastSlot = state.LastSlot; lastFinalizedDay = state.LastFinalizedDay;
        }
        catch (Exception ex)
        {
            // Keep the unreadable file for inspection rather than overwriting it, and start clean.
            logger.LogError(ex, "The attendance staging file could not be read; it was set aside.");
            try { File.Move(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}"); } catch { /* best effort */ }
        }
    }

    // Write to a temp file and swap it in, so a crash never leaves a half-written staging file.
    void Persist()
    {
        var state = new StagingState
        {
            NextVersion = nextVersion, LastSlot = lastSlot, LastFinalizedDay = lastFinalizedDay,
            Cells = cells.Values.OrderBy(c => c.Version).ToList(), Manpower = manpower.Values.OrderBy(m => m.Version).ToList(),
            Notes = notes.Values.OrderBy(n => n.Version).ToList()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, path, overwrite: true);
    }
}

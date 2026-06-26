// Moved from EquipmentChecklist/Models/Enums.cs so that both the server
// (Microsoft.NET.Sdk.Web, net8.0/net9.0) and the MAUI mobile app
// (net9.0-windows / net9.0-android) can reference exactly the same enum
// integer values without dragging a Web-SDK reference into the Android
// build graph. Namespace intentionally unchanged.

namespace EquipmentChecklist.Models;

public enum ChecklistStatus
{
    InProgress = 0,
    Go = 1,           // R – all items OK
    GoButRepair24H = 2, // W – supervisor must sign, repair within 24h
    GoTillNextService = 3, // W – repair by next service (max 30 days)
    NoGo = 4,          // immediate repair, machine immobilised
    Rejected = 5       // supervisor rejected – sent to mechanic for repair
}

public enum ItemStatus
{
    InOrder = 1,  // tick / OK
    Defect = 2    // X  / fault found
}

public enum Shift
{
    Day = 1,
    Afternoon = 2,
    Night = 3
}

public enum UserRole
{
    Admin       = 1,
    Operator    = 2,
    Supervisor  = 3,
    /// <summary>Internal role name kept as "Mechanic" for backward
    /// compatibility with existing data. UI labels say "Artisan" — the
    /// SA mining term operations staff actually use.</summary>
    Mechanic    = 4,

    /// <summary>Plant Maintenance Planner — captures supervisor-approved
    /// defects into SAP / the work-order system and converts each to a
    /// formal jobcard before dispatch. New in Phase 3.</summary>
    Planner     = 5,

    /// <summary>Mine Control Room dispatcher — sees the queue of jobcards
    /// needing an Artisan and assigns the right person based on fleet
    /// (Mota-Engil, Moolmans, etc.) and availability. New in Phase 3.</summary>
    ControlRoom = 6
}

public enum RepairStatus
{
    Pending = 0,
    InProgress = 1,
    AwaitingParts = 2,
    Completed = 3
}

public enum MachineType
{
    ADT = 1,
    ArticulatedWaterTruck = 2,
    DieselBowser = 3,
    Drills = 4,
    Excavator = 5,
    FEL = 6,
    Forklift = 7,
    Grader = 8,
    LDV = 9,
    SRVWaterBowser = 10,
    TrackDozer = 11,
    RDT = 12,
    TruckMountedCrane = 13,
    TLB = 14
}

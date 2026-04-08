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
    Admin = 1,
    Operator = 2,
    Supervisor = 3,
    Mechanic = 4
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

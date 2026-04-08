using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Data;

/// <summary>
/// Primary cloud database context using PostgreSQL.
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<MachineAssignment> MachineAssignments => Set<MachineAssignment>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<ChecklistTemplateItem> ChecklistTemplateItems => Set<ChecklistTemplateItem>();
    public DbSet<ChecklistSubmission> ChecklistSubmissions => Set<ChecklistSubmission>();
    public DbSet<SubmissionItem> SubmissionItems => Set<SubmissionItem>();
    public DbSet<DefectOrder> DefectOrders => Set<DefectOrder>();
    public DbSet<OperatorSupervisorAssignment> OperatorSupervisorAssignments => Set<OperatorSupervisorAssignment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Machine
        builder.Entity<Machine>(e =>
        {
            e.HasIndex(m => m.MachineNumber).IsUnique();
            e.HasOne(m => m.Template)
             .WithOne(t => t.Machine)
             .HasForeignKey<ChecklistTemplate>(t => t.MachineId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // MachineAssignment
        builder.Entity<MachineAssignment>(e =>
        {
            e.HasOne(a => a.Machine).WithMany(m => m.Assignments)
             .HasForeignKey(a => a.MachineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Operator).WithMany(u => u.Assignments)
             .HasForeignKey(a => a.OperatorId).OnDelete(DeleteBehavior.Restrict);
        });

        // ChecklistSubmission
        builder.Entity<ChecklistSubmission>(e =>
        {
            e.HasIndex(s => s.LocalId).IsUnique(); // support offline upserts
            e.HasOne(s => s.Machine).WithMany(m => m.Submissions)
             .HasForeignKey(s => s.MachineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Operator).WithMany(u => u.Submissions)
             .HasForeignKey(s => s.OperatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Supervisor).WithMany()
             .HasForeignKey(s => s.SupervisorId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(s => s.Mechanic).WithMany()
             .HasForeignKey(s => s.MechanicId).OnDelete(DeleteBehavior.SetNull);
        });

        // SubmissionItem
        builder.Entity<SubmissionItem>(e =>
        {
            e.HasOne(i => i.Submission).WithMany(s => s.Items)
             .HasForeignKey(i => i.SubmissionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.TemplateItem).WithMany()
             .HasForeignKey(i => i.TemplateItemId).OnDelete(DeleteBehavior.Restrict);
        });

        // DefectOrder
        builder.Entity<DefectOrder>(e =>
        {
            e.HasOne(d => d.Submission).WithMany(s => s.DefectOrders)
             .HasForeignKey(d => d.SubmissionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.AssignedMechanic).WithMany()
             .HasForeignKey(d => d.AssignedMechanicId).OnDelete(DeleteBehavior.SetNull);
        });

        // ChecklistSubmission – RejectedMechanic
        builder.Entity<ChecklistSubmission>(e =>
        {
            e.HasOne(s => s.RejectedMechanic).WithMany()
             .HasForeignKey(s => s.RejectedMechanicId).OnDelete(DeleteBehavior.SetNull);
        });

        // OperatorSupervisorAssignment
        builder.Entity<OperatorSupervisorAssignment>(e =>
        {
            e.HasOne(a => a.Operator).WithMany()
             .HasForeignKey(a => a.OperatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Supervisor).WithMany()
             .HasForeignKey(a => a.SupervisorId).OnDelete(DeleteBehavior.Restrict);
        });

        // Seed checklist templates for all 14 machine types
        //SeedTemplates(builder);
    }

    private static void SeedTemplates(ModelBuilder builder)
    {
        var templates = new List<(MachineType type, string name, string[] items)>
        {
            (MachineType.ADT, "Articulated Dump Truck", new[]{
                "OPERATOR LICENCE","STOP BLOCKS","FIRE EXTINGUISHER","SEAT BELTS (IN USE)",
                "HEAD LIGHTS","ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS",
                "BRAKE LIGHTS","DOORS/HANDLES","HAND RAILS","WHEEL NUTS","PINS IN POSITION AND LOCKED",
                "LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","AUDIBLE BIN RAISE ALARM",
                "BRAKE TEST (SERVICE BRAKE)","BRAKE TEST (PARK BRAKE)",
                "BRAKE TEST RAMP WHEN ENTERING PIT","RADIO TWO-WAY","ISOLATION POINT",
                "KEY CONTROL","AIR CONDITIONER","OIL LEVEL","DASHBOARD INDICATORS/INSTRUMENTS",
                "VISIBLE LEAKS","WINDOWS","TIRE CONDITION","STEPS","SEATS","WINDSCREEN WIPER",
                "MIRRORS","COOLANT LEVEL","RADIATOR NOT CLOGGED","HYDRAULIC OIL LEVEL",
                "REFLECTIVE TAPE CONDITION","FUEL LEVEL","NEW BUMP MARKS"
            }),
            (MachineType.TLB, "TLB", new[]{
                "OPERATOR LICENCE","STOP BLOCKS","FRONT LIGHTS","REAR LIGHTS","INDICATOR LIGHTS",
                "ROTATING LIGHT","ALL MIRRORS","DASH BOARD WARNING LIGHTS","HOOTER","REVERSE HOOTER",
                "STEERING/STEER CONTROL","BRAKES","KEY CONTROL / PROXY","FIRE EXTINGUISHER",
                "GAUGES","TYRES AND WHEEL NUTS","SEAT BELT","CONTROLS",
                "PARK BRAKE IN WORKING CONDITION","ISOLATION POINT",
                "AIR CONDITIONER","RADIO TWO-WAY (If intended for PIT)","DIGGING BOOM ASSEMBLY",
                "BUCKET AND CUTTING EDGES","OIL / WATER LEAKS","WINDOWS","OUT RIGGERS",
                "NEW BUMP MARKS","TOW BAR & PIN","REFLECTIVE TAPE CONDITION","ALL PIPES",
                "SCREEN WIPER","FUEL LEVEL","DOORS & HANDLES","SEATS","STEPS","Recent body damages"
            }),
            (MachineType.Excavator, "Excavator", new[]{
                "OPERATOR LICENCE","FIRE EXTINGUISHER","SEAT BELTS","HEAD LIGHTS",
                "ROTATING / FLASH LIGHT","AREA LIGHTS","DASH BOARD WARNING LIGHTS","HOOTER",
                "TRAVEL ALARM","SWING BRAKES","CONTROLS","ALL PIPES","ALL MIRRORS",
                "RADIO TWO-WAY","ISOLATION POINT","KEY CONTROL","EMERGENCY STOP CONDITION",
                "AIR CONDITIONER","DOORS & HANDLES","STEPS","HAND GRIPS","WINDOWS","SEATS",
                "OIL LEAKS","TRACKS","REFLECTIVE TAPE CONDITION","BUCKET","ROLLERS","NEW BUMP MARKS"
            }),
            (MachineType.FEL, "Front End Loader", new[]{
                "OPERATOR LICENCE","STOP BLOCKS","FIRE EXTINGUISHER","SEAT BELT","HEAD LIGHTS",
                "ACCESS LIGHTS","REAR LIGHTS","INDICATOR LIGHTS","HAZARD LIGHTS","ALL MIRRORS",
                "DASH BOARD WARNING LIGHTS","HOOTER","REVERSE HOOTER","STEERING/STEER CONTROL",
                "BRAKES","KEY CONTROL / PROXY","GAUGES","TYRES","CONTROLS",
                "BRAKE TEST RAMP WHEN ENTERING PIT","ROTATING LIGHT",
                "RADIO TWO-WAY (Only when entering Pit)","ISOLATION POINT","AIR CONDITIONER",
                "DOORS & HANDLES","HAND GRIPS","HAND RAILS","SCREEN WIPER","FUEL LEVEL",
                "ALL PIPES","SEATS","OIL / WATER LEAKS","WINDOWS","STEPS",
                "REFLECTIVE TAPE CONDITION","BUCKET","TOW BAR & PIN","NEW BUMP MARKS"
            }),
            (MachineType.Grader, "Grader", new[]{
                "OPERATORS LICENCE","SEAT BELT","FIRE EXTINGUISHER","HEAD LIGHTS","ROTATING LIGHT",
                "INDICATOR LIGHTS","REVERSE LIGHTS","HAZARD LIGHTS","REAR LIGHTS","MIRRORS",
                "DASH BOARD WARNING LIGHTS","HOOTER","REVERSE HOOTER","GAUGES",
                "STEERING/STEER CONTROL","BRAKES","CONTROLS","ISOLATION POINT","KEY CONTROL",
                "RADIO TWO-WAY","AIR CONDITIONER","DOORS & HANDLES","TYRES","HAND GRIPS",
                "SCREEN WIPER","FUEL LEVEL","ALL PIPES","OIL / WATER LEAKS","WINDOWS","STEPS",
                "SEATS","REFLECTIVE TAPE CONDITION","BLADE & RIPPER","HOUR METER","NEW BUMP MARKS"
            }),
            (MachineType.RDT, "773 Haul Truck / RDT", new[]{
                "OPERATOR LICENCE","STOP BLOCKS","FIRE EXTINGUISHER","ALL SEAT BELTS",
                "HEAD LIGHTS","REVERSE LIGHTS","INDICATOR LIGHTS","REAR LIGHTS",
                "KEY CONTROL / PROXY","EMERGENCY STOP CONDITION","ALL MIRRORS",
                "DASH BOARD WARNING LIGHTS","DUMP BODY UP BUZZER (At first dump)","HOOTER",
                "REVERSE HOOTER","DIRECTIONAL HOOTER","STEERING","BRAKES","GAUGES","TYRES",
                "CONTROLS","RADIO TWO-WAY","AIR CONDITIONER","DOORS & HANDLES",
                "HAND RAILS & KICK PLATES","ACCESS LIGHT","SCREEN WIPER","OIL / WATER LEAKS",
                "ALL PIPES","WINDOWS","SEATS","REFLECTIVE TAPE CONDITION","STEPS","HOUR METER",
                "NEW BUMP MARKS"
            }),
            (MachineType.Forklift, "Forklift", new[]{
                "OPERATOR LICENCE","STOPBLOCKS","WHEEL NUTS","FORK CONDITION","LIFTING CHAIN CONDITION",
                "PINS IN POSITION AND LOCKED","SEAT BELT","EMERGENCY TRIANGLE",
                "LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS FRONT",
                "ROTATING LIGHT","INDICATOR LIGHTS","REAR LIGHTS","BRAKE LIGHTS",
                "BRAKE TEST (PARK BRAKE)","BRAKE TEST (SERVICE BRAKE)","FIRE EXTINGUISHER",
                "TYRE CONDITION","DASHBOARD INDICATORS","MIRRORS","SEATS","VISIBLE LEAKS",
                "HYDRAULIC OIL","OIL LEVEL","COOLANT LEVEL","RADIATOR NOT CLOGGED","NEW BUMP MARKS"
            }),
            (MachineType.TrackDozer, "Track Dozer", new[]{
                "OPERATOR LICENCE","SEAT BELT","FIRE EXTINGUISHER","HEAD LIGHTS","ROTATING LIGHT",
                "ACCESS LIGHTS","REAR LIGHTS","MIRROR","DASH BOARD WARNING LIGHTS","HOOTER",
                "REVERSE HOOTER","STEER CONTROL","BRAKES","CONTROLS","GAUGES","ISOLATION POINT",
                "KEY CONTROL","AIR CONDITIONER","DOORS & HANDLES","GUARDS","RADIO TWO-WAY",
                "SCREEN WIPER","FUEL LEVEL","ALL PIPES","SEATS","WINDOWS","STEPS",
                "OIL / WATER LEAKS","TRACKS","HAND GRIPS","REFLECTIVE TAPE CONDITION",
                "BLADE/RIPPER","NEW BUMP MARKS"
            }),
            (MachineType.LDV, "LDV / Mini Bus / Trooper / Crew Cab / Sedan", new[]{
                "OPERATOR LICENCE","WHEEL NUTS","FIRE EXTINGUISHER","EMERGENCY TRIANGLE",
                "SEAT BELTS (IN USE)","HOOTER","REVERSE HOOTER","HEAD LIGHTS","INDICATORS",
                "BRAKE LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE TEST (FOOT BRAKE)",
                "BRAKE TEST (EMERGENCY BRAKE)","VISIBLE LEAKS","TYRE CONDITION",
                "DASHBOARD INDICATORS/INSTRUMENTS","WINDOWS","MIRRORS","SEATS CONDITION",
                "SCREEN WIPER","BRAKE TEST RAMP WHEN ENTERING PIT","FLAG","ROTATING LIGHT",
                "TWO WAY RADIO","REFLECTIVE TAPE CONDITION",
                "OIL LEVEL","COOLANT LEVEL","BRAKE FLUID LEVEL","SCREEN WIPER WATER LEVEL",
                "RADIATOR NOT CLOGGED"
            }),
            (MachineType.ADT, "Articulated Water Truck", new[]{
                "OPERATOR LICENCE","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
                "HAND RAILS","DOORS/HANDLES","STOP BLOCKS","WHEEL NUTS","SEAT BELTS (IN USE)",
                "LEVERS/JOYSTICKS/STEER CONTROL","STEERING/STEER CONTROL","HOOTER",
                "REVERSE HOOTER","HEAD LIGHTS","ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS",
                "REVERSE LIGHTS","BRAKE LIGHTS","BRAKE TEST (SERVICE BRAKE)",
                "BRAKE TEST (PARK BRAKE)","AIR CONDITIONER","OIL LEVEL","DASHBOARD INSTRUMENTS",
                "VISIBLE LEAKS","WINDOWS","TIRE CONDITION","STEPS","SEATS","SCREEN WIPER",
                "MIRRORS","COOLANT LEVEL","RADIATOR NOT CLOGGED","HYDRAULIC OIL LEVEL","FUEL LEVEL",
                "NEW BUMP MARKS","BRAKE TEST RAMP WHEN ENTERING PIT","REFLECTIVE TAPE CONDITION",
                "FLAG","RADIO TWO-WAY"
            }),
            (MachineType.DieselBowser, "Diesel Bowser", new[]{
                "OPERATOR LICENCE","WHEEL NUTS","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
                "STOP BLOCKS","EMERGENCY TRIANGLE (EXCLUDING RED PERMIT AREA)","SEAT BELTS (IN USE)",
                "LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
                "ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
                "BRAKE TEST (SERVICE BRAKE)","PPE","ISOLATION POINT","PUMPS","GUARDS",
                "HOSE CONNECTORS","HOSE FITTINGS","PTO","NOZZLE","BRAKE TEST (PARK BRAKE)",
                "DOORS/HANDLES","AIR CONDITIONER","TWO WAY RADIO","VISIBLE LEAKS","MIRRORS",
                "WINDOWS","TIRE CONDITION","SEATS","STEPS","SCREEN WIPER","OIL LEVEL",
                "DASHBOARD INDICATORS/INSTRUMENTS","HYDRAULIC OIL","COOLANT LEVEL",
                "RADIATOR NOT CLOGGED","PTO","NEW BUMP MARKS","BRAKE TEST RAMP WHEN ENTERING PIT",
                "RADIO TWO-WAY","FLAG","REFLECTIVE TAPE","DIESEL READINGS"
            }),
            (MachineType.SRVWaterBowser, "SRV / Water Bowser", new[]{
                "OPERATOR LICENCE","WHEEL NUTS","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
                "STOP BLOCKS","EMERGENCY TRIANGLE (EXCLUDING RED PERMIT AREA)","SEAT BELTS (IN USE)",
                "LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
                "ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
                "BRAKE TEST (SERVICE BRAKE)","PUMPS","GUARDS","HOSE CONNECTORS","HOSES AND FITTINGS",
                "BRAKE TEST (PARK BRAKE)","DOORS/HANDLES","AIR CONDITIONER","OIL LEVEL",
                "VISIBLE LEAKS","MIRRORS","WINDOWS","TIRE CONDITION","SEATS","STEPS","SCREEN WIPER",
                "GUARDS","DASHBOARD INDICATORS/INSTRUMENTS","HYDRAULIC OIL","COOLANT LEVEL",
                "RADIATOR NOT CLOGGED","NEW BUMP MARKS","BRAKE TEST RAMP WHEN ENTERING PIT",
                "RADIO TWO-WAY","FLAG","REFLECTIVE TAPE"
            }),
            (MachineType.TruckMountedCrane, "Truck-Tractor Mounted Crane", new[]{
                "OPERATOR LICENCE","WHEEL NUTS","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
                "STOP BLOCKS","EMERGENCY TRIANGLE (EXCLUDING RED PERMIT AREA)","SEAT BELTS (IN USE)",
                "LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
                "ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
                "BRAKE TEST (SERVICE BRAKE)","HOOK SAFETY LATCH","OUTRIGGERS",
                "PINS IN POSITION AND LOCKED","LEVERS/JOYSTICKS","BRAKE TEST (PARK BRAKE)",
                "DOORS/HANDLES","TIRE CONDITION","DASHBOARD INDICATORS/INSTRUMENTS","VISIBLE LEAKS",
                "MIRRORS","WINDOWS","NEW BUMP MARKS","SEATS","STEPS","SCREEN WIPER",
                "AIR CONDITIONER","LOAD CHART","OIL LEVEL","COOLANT LEVEL",
                "RADIATOR NOT CLOGGED","HYDRAULIC OIL","BRAKE TEST RAMP WHEN ENTERING PIT",
                "RADIO TWO-WAY","FLAG","REFLECTIVE TAPE"
            }),
            (MachineType.Drills, "Drills", new[]{
                "OPERATOR LICENCE","FIRE EXTINGUISHER","FRONT & REAR LIGHTS","ROTATING LIGHT",
                "TRAM HOOTER","RADIO TWO-WAY","DASH BOARD WARNING LIGHTS","HOOTER",
                "DRIFTER SLIDES","DRIFTER CABLE","SEAT BELT","KEY CONTROL","CONTROLS",
                "EMERGENCY STOP CONDITION","BOOM CONDITION","ISOLATION POINT","HOSE CONNECTORS",
                "HOSES","ISOLATOR VALVES","GUARDS","AIR CONDITIONER","DOOR & HANDLES",
                "DUST COLLECTION","DOORS/HANDLES","MIRRORS","OIL / FUEL / WATER LEAKS","SEAT",
                "STEPS","REFLECTIVE TAPE CONDITION","TRACKS & SPROCKETS","WINDOWS & WIPERS",
                "CYLINDERS","TOW BAR & PIN","CAROUSAL","NEW BUMP MARKS","FUEL LEVEL"
            })
        };

        int templateId = 1, itemId = 1;
        var templateEntities = new List<object>();
        var itemEntities = new List<object>();

        foreach (var (type, name, items) in templates)
        {
            templateEntities.Add(new { Id = templateId, MachineType = type, Name = name, MachineId = templateId });
            for (int i = 0; i < items.Length; i++)
            {
                itemEntities.Add(new
                {
                    Id = itemId++,
                    TemplateId = templateId,
                    ItemName = items[i],
                    Section = "General",
                    SortOrder = i + 1,
                    IsNoGoItem = IsNoGoItem(items[i])
                });
            }
            templateId++;
        }

        builder.Entity<ChecklistTemplate>().HasData(templateEntities.ToArray());
        builder.Entity<ChecklistTemplateItem>().HasData(itemEntities.ToArray());
    }

    private static bool IsNoGoItem(string item)
    {
        var noGoItems = new[]
        {
            "OPERATOR LICENCE","SEAT BELT","SEAT BELTS","BRAKES","BRAKE TEST",
            "FIRE EXTINGUISHER","KEY CONTROL","EMERGENCY STOP"
        };
        return noGoItems.Any(n => item.Contains(n, StringComparison.OrdinalIgnoreCase));
    }
}

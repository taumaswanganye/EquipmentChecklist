# Belfast Coal Mine – Digital Equipment Checklist System

## Overview
Full-stack C# MVC + REST API system for digitising pre-use equipment inspections across 14 machine types.  
Supports **offline-first** operation via SQLite sync, with PostgreSQL as the authoritative cloud database.

---

## Roles
| Role | Capabilities |
|------|-------------|
| **Admin** | Add machines, assign operators, manage users, configure checklists, view all data |
| **Operator/Driver** | Complete daily pre-use checklist, declare fitness to operate |
| **Supervisor** | Review submissions, sign off GO-BUT (W) items, approve or reject |
| **Mechanic** | View defects assigned to them, update repair status, order parts |

---

## Machine Types (14 sheets from checklist)
ADT, Articulated Water Truck, Diesel Bowser, Drills, Excavator, FEL,  
Forklift, Grader, LDV, SRV Water Bowser, Track Dozer, RDT, Truck Mounted Crane, TLB

---

## Status Flow
```
Operator submits checklist
       ↓
All items OK? ──YES──→ GO (green) – machine cleared
       ↓ NO
Any NO-GO item? ──YES──→ NO-GO (red) – machine IMMOBILISED → Mechanic assigned
       ↓ NO
GO-BUT items (W)? ──YES──→ Supervisor must sign → GO-BUT (amber) → Mechanic repair within 24h
                                                                   or GO-till-next-service (30 days)
```

---

## Tech Stack
- **Backend**: ASP.NET Core 8 MVC + Web API
- **ORM**: Entity Framework Core 8
- **Online DB**: PostgreSQL (Npgsql)
- **Offline DB**: SQLite (for field tablets/phones)
- **Auth**: ASP.NET Core Identity + JWT for API
- **Sync**: Background sync service (SQLite → PostgreSQL)
- **Frontend**: Razor Views + Vanilla JS (offline-capable via Service Worker)

---

## Project Structure
```
EquipmentChecklist/
├── Controllers/
│   ├── AccountController.cs
│   ├── AdminController.cs
│   ├── ChecklistController.cs
│   ├── MechanicController.cs
│   ├── SupervisorController.cs
│   └── api/
│       ├── ChecklistApiController.cs
│       ├── MachineApiController.cs
│       └── SyncApiController.cs
├── Models/
│   ├── Machine.cs
│   ├── Employee.cs
│   ├── ChecklistTemplate.cs
│   ├── ChecklistItem.cs
│   ├── ChecklistSubmission.cs
│   ├── SubmissionItem.cs
│   ├── DefectOrder.cs
│   └── Enums.cs
├── Data/
│   ├── ApplicationDbContext.cs        ← PostgreSQL
│   └── LocalDbContext.cs              ← SQLite (offline)
├── Services/
│   ├── SyncService.cs
│   ├── NotificationService.cs
│   └── ChecklistService.cs
├── DTOs/
│   └── SyncPayloadDto.cs
├── wwwroot/
│   ├── css/site.css
│   ├── js/checklist.js
│   └── js/sync.js
├── Views/  (Razor)
├── Program.cs
├── appsettings.json
└── EquipmentChecklist.csproj
```

# Manpower Management

Internal ASP.NET Core 8 Razor Pages application for workshop manpower and leave reporting.

## Run locally

For normal use, open `../ManpowerManagement-Run/ManpowerManagement.exe`, leave its console window open, then visit `http://localhost:5257`. Keep the other files in `ManpowerManagement-Run` together with the executable; they contain the web assets and configuration. The `ManpowerManagement` folder is the source code used when making changes.

To rebuild the run folder after changing the source, run `dotnet publish ManpowerManagement/ManpowerManagement.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None --output ManpowerManagement-Run` from the workspace root.

### Development

1. Update `ConnectionStrings:ManpowerDb` in `appsettings.json` if a different SQL Server database is required.
2. For normal development, run `dotnet restore` once, then start the app with `./run-dev.ps1` from this folder and open `http://localhost:5257`.
   `dotnet watch` detects saved changes to Razor, C#, CSS, and JavaScript files, then applies Hot Reload when possible or restarts the app automatically. Refresh the browser after the terminal reports that the update is ready; do not run the app again manually. Use `dotnet run` only when file watching is not needed.
3. The first start applies EF Core migrations and seeds workshop data and the initial Admin account: `admin` / `ChangeMe!123`. Change this password before production use.

### Database

- The app connects to the shared `TaMFGdb` database on `svr120a`, which already hosts tables for another system (`issue_entries`, `tickets`, `users`, etc.) in the `dbo` schema.
- All Manpower Management tables (Identity + domain tables) live in a dedicated `mp` schema (`AppDbContext.OnModelCreating` sets `HasDefaultSchema("mp")`) so they never collide with the existing tables.
- Schema is managed with EF Core Migrations (not `EnsureCreated`), since `EnsureCreated` only checks whether *any* table exists in the database and would otherwise skip creation because of the pre-existing `dbo` tables. To add a migration after model changes: `dotnet tool install --global dotnet-ef` (once), then `dotnet ef migrations add <Name>` from this folder.

### Current validation status

- `dotnet build` completes successfully.
- Verified end-to-end on 2026-09-18: TCP 1433 to `svr120a` is reachable, the TLS-encrypted connection succeeds, EF Core migrations apply cleanly to the `mp` schema, seed data (roles, Admin user, Workshops) is created, and the app serves `/Account/Login` with HTTP 200.

## Deployment

Deploy behind IIS on the corporate Intranet and enforce company-network access with IIS/network firewall rules. Set the SQL connection string through an IIS environment variable or production configuration; do not commit credentials.

## Excel imports

- Employee: `EmployeeId`, `FullName`, `Workshop`, `StartDate`, `EmploymentType`
- Calendar: `WorkDate`, `Workshop`, `IsWorkingDay`

## Daily working-hour summary

Full hour-counting rules (Thai): [docs/HOUR_COUNTING.md](docs/HOUR_COUNTING.md).

There is **no Submit button** on Attendance (By Shift or Pregnant). Supervisors keep working in the grid, or paste from Excel, and only key exceptions:

- **Everyone counts 100% by default.** On a working day each person counts 10.67 hours (Pregnant: 8.00, Monday to Friday) from the start of the business day. Leave, absent, other hours and transfers are keyed on top and reduce the count. Only these exceptions are ever stored in `mp.AttendanceEntries`.
- **Edits are not written to SQL one by one.** The grid saves each change as you type into a staging file, `App_Data/attendance-staging.json` next to the app (so it survives a restart). The grid, Dashboard and New Hires all read SQL plus the staged changes, so every page is current as soon as it is loaded; only the daily hour summary (Total / Indirect / Direct) waits for the day to close.
- **07:00 and 19:00:** the app copies the staged changes to SQL in one save, then deletes the copied items from the staging file. If SQL refuses, nothing is deleted and the copy is retried every minute. After downtime the missed copy runs when the app starts again, so keep the app running. Each copy writes one `Flushed staged attendance` audit row.
- **New day (07:00):** the closed business day is counted straight away from effective attendance. Every workshop and shift with anyone scheduled gets its row in `mp.ShiftManpowers` (one row per date, workshop and shift) even if nobody keyed anything. `TotalHours` includes all working hours, including attendance leaders; Pregnant hours follow the employee's roster shift. `IndirectHours` starts from the three indirect headcount bands (10.67, 8.41 and 7.33 hours, keyed in the grid under Indirect MP) plus leader hours; if the bands were not keyed, only leader hours count and the grid marks the day. Direct hours are always `TotalHours - IndirectHours`. On the Day shift every employee is indirect: `IndirectHours` equals `TotalHours` and the below-56T half count does not apply.
- The 19:00 copy only saves changes; the open day is counted when it closes at 07:00.

Admins can correct a closed day's aggregate indirect hours in the Daily working hours table on Attendance. The correction remains in place when attendance is edited; the total hours refresh automatically. Reset returns indirect hours to the band calculation.

### Tests

`dotnet test ManpowerManagement.Tests` runs the hour counting, staging and 07:00 / 19:00 copy against an in-memory database (never the shared SQL Server).

## New hire monitoring

Open **New Hires** in the menu. Leaders check in first (same check-in as Attendance); Admin and Resignation Approvers log in. Everyone who can open the page sees **new hires in every line**. Changes are limited by line: a leader can tag New Employee, record output and record probation decisions only for their own line, Admin for every line, and HR acknowledges decisions.

- **New hire** = an active employee who was tagged **New Employee = Yes** when added. The choice is mandatory when adding on Employees (single form and paste grid), so people who were only forgotten in the system or transferred from another department are added as **No** and are never monitored. Choosing **No** is open to everyone; setting or changing the tag to **Yes** needs Admin or a leader checked in for that workshop's line. The tag can be changed later with Edit or Edit grid. Age is counted from the **Start Date** (the day they actually started, required for a Yes), never from when the record was created. Existing employees default to No until someone tags them.
- **Probation**: the leader decides before day 60 (pass, extend up to day 119 in total, or do not accept). HR acknowledges afterwards; acknowledging a pass makes the employee `Permanent`. "Do not accept" submits a normal resignation ticket for an approver to approve.
- **Start date** is editable on Employees (single form, paste grid column, inline grid edit) and the CSV export includes Start Date and New Employee.
- **Daily output** (kps) is keyed on New Hires > Enter daily output and shown as a daily chart and 7-day average on each new hire. There is no target or learning curve.
- **At risk** flags come from Attendance: leave to review, absent days in a row, no-show at the start, and leave rate over a window. Thresholds are editable on **Admin > Early-warning rules**.
- **Retention** by start month shows the share still employed at day 30 / 60 / 90 / 119, counting only people who have reached that day.


using ManpowerManagement.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ManpowerManagement.Data;

public static class SeedData
{
    public static readonly string[] EmployeeWorkshopNames =
    [
        "Pellet Line", "Pellet Assembly", "Anodization", "Polymer",
        "PSLB", "PSLA", "GPSP2", "FPSA", "FPSB"
    ];

    // Workshops selectable for Day-shift employees: (label shown, workshop name stored)
    public static readonly (string Label, string Name)[] DayWorkshops =
    [
        ("Total Production", "Total Production"), ("Chemical Line", "Total Chemical Line"),
        ("Assembly Line", "Total Assembly Line"), ("Equipment Support", "Equipment Support"),
        ("Staff Production", "Staff Production")
    ];

    public static async Task InitialiseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var role in new[] { Roles.Admin, Roles.Approver })
            if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));
        var admin = await users.FindByNameAsync("admin");
        if (admin is null)
        {
            admin = new ApplicationUser { UserName = "admin", Email = "admin@company.local", EmailConfirmed = true };
            await users.CreateAsync(admin, "ChangeMe!123");
            await users.AddToRoleAsync(admin, Roles.Admin);
        }
        if (!await db.Workshops.AnyAsync())
        {
            var production = new Workshop { Name = "Total Production" };
            var chemical = new Workshop { Name = "Total Chemical Line", Parent = production };
            var assembly = new Workshop { Name = "Total Assembly Line", Parent = production };
            var psl = new Workshop { Name = "PSL", Parent = assembly };
            var fps = new Workshop { Name = "FPS", Parent = assembly };
            db.Workshops.AddRange(production, chemical, assembly, psl, fps,
                new Workshop { Name = "Pellet Line", Parent = chemical },
                new Workshop { Name = "Pellet Assembly", Parent = chemical },
                new Workshop { Name = "Anodization", Parent = chemical },
                new Workshop { Name = "Polymer", Parent = chemical },
                new Workshop { Name = "PSLA", Parent = psl },
                new Workshop { Name = "PSLB", Parent = psl },
                new Workshop { Name = "GPSP2", Parent = assembly },
                new Workshop { Name = "FPSA", Parent = fps },
                new Workshop { Name = "FPSB", Parent = fps });
            await db.SaveChangesAsync();
        }
        else
        {
            await RealignWorkshopHierarchyAsync(db);
        }
        var existingWorkshopNames = await db.Workshops.Select(x => x.Name).ToListAsync();
        foreach (var name in EmployeeWorkshopNames.Concat(DayWorkshops.Select(x => x.Name)))
            if (!existingWorkshopNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                db.Workshops.Add(new Workshop { Name = name });
        await db.SaveChangesAsync();
    }

    static async Task<Workshop> GetOrCreate(AppDbContext db, List<Workshop> all, string name, int? parentId)
    {
        var found = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            if (found.ParentId != parentId) found.ParentId = parentId;
            return found;
        }
        var created = new Workshop { Name = name, ParentId = parentId };
        db.Workshops.Add(created);
        await db.SaveChangesAsync();
        all.Add(created);
        return created;
    }

    static async Task RealignWorkshopHierarchyAsync(AppDbContext db)
    {
        var all = await db.Workshops.ToListAsync();
        var production = await GetOrCreate(db, all, "Total Production", null);
        var chemical = await GetOrCreate(db, all, "Total Chemical Line", production.Id);
        var assembly = await GetOrCreate(db, all, "Total Assembly Line", production.Id);
        var psl = await GetOrCreate(db, all, "PSL", assembly.Id);
        var fps = await GetOrCreate(db, all, "FPS", assembly.Id);

        await GetOrCreate(db, all, "Pellet Line", chemical.Id);
        await GetOrCreate(db, all, "Pellet Assembly", chemical.Id);
        await GetOrCreate(db, all, "Anodization", chemical.Id);
        await GetOrCreate(db, all, "Polymer", chemical.Id);
        await GetOrCreate(db, all, "PSLA", psl.Id);
        await GetOrCreate(db, all, "PSLB", psl.Id);
        await GetOrCreate(db, all, "GPSP2", assembly.Id);
        await GetOrCreate(db, all, "FPSA", fps.Id);
        await GetOrCreate(db, all, "FPSB", fps.Id);

        // Retire legacy duplicate rows in favor of the canonical leaves above, moving any
        // employees/history/attendance already pointing at them instead of deleting the rows.
        var legacyMap = new (string LegacyName, string CanonicalName)[]
        {
            ("Pallet Assembly", "Pellet Assembly"),
            ("Anodization – Check I", "Anodization"),
            ("GPS", "GPSP2"),
        };
        foreach (var (legacyName, canonicalName) in legacyMap)
        {
            var legacy = all.FirstOrDefault(x => x.Name.Equals(legacyName, StringComparison.OrdinalIgnoreCase));
            var canonical = all.FirstOrDefault(x => x.Name.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));
            if (legacy is null || canonical is null || legacy.Id == canonical.Id) continue;
            await db.Employees.Where(x => x.CurrentWorkshopId == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentWorkshopId, canonical.Id));
            await db.EmployeeHistories.Where(x => x.WorkshopId == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkshopId, canonical.Id));
            await db.AttendanceEntries.Where(x => x.WorkshopId == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkshopId, canonical.Id));
            await db.WorkCalendars.Where(x => x.WorkshopId == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkshopId, canonical.Id));
            await db.ResignationTickets.Where(x => x.WorkshopId == legacy.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.WorkshopId, canonical.Id));
            db.Workshops.Remove(legacy);
            all.Remove(legacy);
        }
        await db.SaveChangesAsync();
    }
}

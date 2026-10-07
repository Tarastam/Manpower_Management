using ManpowerManagement.Data;
using ManpowerManagement.Models;
using ManpowerManagement.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = 20000;
});
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<LeaderSessionService>();
builder.Services.AddScoped<WorkforceDashboardService>();
builder.Services.AddSingleton<AttendanceStagingStore>();
builder.Services.AddScoped<EffectiveAttendance>();
builder.Services.AddScoped<DailyHoursService>();
builder.Services.AddScoped<AttendanceFlushService>();
builder.Services.AddScoped<NewHireMonitorService>();
builder.Services.AddHostedService<AttendanceFlushWorker>();
builder.Services.AddHostedService<ResignationWorker>();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("ManpowerDb"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure();
            // The production database is below compatibility level 130, which has no OPENJSON. Without this,
            // EF Core 8 translates list.Contains(...) to OPENJSON ... WITH and SQL Server rejects it.
            sqlOptions.UseCompatibilityLevel(120);
        }));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options => options.LoginPath = "/Account/Login");
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(Roles.Admin));
    options.AddPolicy("TicketsManage", policy => policy.RequireRole(Roles.Admin, Roles.Approver));
});
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    options.Conventions.AuthorizeFolder("/SpecialGroups", "AdminOnly");
    options.Conventions.AuthorizePage("/Tickets/Manage", "TicketsManage");
});
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
{
    options.MaxModelBindingCollectionSize = 20000;
});

var app = builder.Build();
await SeedData.InitialiseAsync(app.Services);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

app.MapRazorPages();

app.Run();

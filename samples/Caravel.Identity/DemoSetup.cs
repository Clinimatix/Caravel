using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Caravel.IdentitySample;

public static class DemoSetup
{
    public static async Task<bool> SeedDemoAsync(this WebApplication app, string[] args)
    {
        if (args.Contains("--seed-demo-workspace", StringComparer.Ordinal))
        {
            if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo workspaces require Development.");
            var name = Environment.GetEnvironmentVariable("CARAVEL_DEMO_USER")
                ?? throw new InvalidOperationException("Set CARAVEL_DEMO_USER to an existing synthetic user.");
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await users.FindByNameAsync(name) ?? throw new InvalidOperationException("Create the synthetic user first.");
            var db = scope.ServiceProvider.GetRequiredService<IdentityContext>();
            if (await db.Set<WorkspaceMember>().AnyAsync(x => x.UserId == user.Id))
                throw new InvalidOperationException("The synthetic user already has a workspace.");
            var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Sample workspace" };
            db.Add(workspace);
            db.Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserId = user.Id, Active = true, CanComplete = true });
            db.Add(new WorkItem { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Title = "Prepare a sample summary" });
            await db.SaveChangesAsync();
            Console.WriteLine("Synthetic workspace and work item created.");
            return true;
        }
        if (args.Contains("--seed-demo-user", StringComparer.Ordinal))
        {
            if (!app.Environment.IsDevelopment())
                throw new InvalidOperationException("Demo-user provisioning is available only in Development.");
            var userName = Environment.GetEnvironmentVariable("CARAVEL_DEMO_USER")
                ?? throw new InvalidOperationException("Set CARAVEL_DEMO_USER for the synthetic account.");
            var password = Environment.GetEnvironmentVariable("CARAVEL_DEMO_PASSWORD")
                ?? throw new InvalidOperationException("Set CARAVEL_DEMO_PASSWORD for the synthetic account.");
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var result = await users.CreateAsync(new IdentityUser { UserName = userName, Email = userName, EmailConfirmed = true }, password);
            if (!result.Succeeded)
                throw new InvalidOperationException("Demo-user creation failed: " + string.Join(", ", result.Errors.Select(error => error.Code)));
            Console.WriteLine("Synthetic demo user created. This sample does not send verification email.");
            return true;
        }
        return false;
    }
}

using KDS.Dashboard.Components;
using KDS.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddKubernetes(builder.Configuration);
builder.Services.AddSingleton<IDeploymentCommandRunner, DeploymentCommandRunner>();
builder.Services.AddSingleton<ILoadTestFactory, LoadTestFactory>();
builder.Services.AddSingleton<TimelineStore>();
builder.Services.AddSingleton<RunArchive>();
builder.Services.AddSingleton<ClusterMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClusterMonitor>());
builder.Services.AddSingleton<HelmService>();
builder.Services.AddSingleton<DeploymentTestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeploymentTestService>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
using FocusLock.Service.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FocusLockGuard");
builder.Services.AddSingleton<SecureStateStore>();
builder.Services.AddSingleton<FocusAuthorityEngine>();
builder.Services.AddHostedService<GuardWorker>();
builder.Services.AddHostedService<PipeServerWorker>();

var host = builder.Build();
host.Run();

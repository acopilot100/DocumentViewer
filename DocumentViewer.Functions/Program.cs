using DocumentViewer.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(services =>
    DocumentBlobStorage.CreateClient(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<DocumentBlobStorage>();

builder.Build().Run();

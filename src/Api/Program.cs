using Ecommerce.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

// Consultado pelo Caddy antes de emitir certificado on-demand (ADR-0004).
// Até o PBI "Domínio próprio com SSL automático", nenhum domínio é autorizado.
app.MapGet("/internal/tls/ask", (string domain) => Results.NotFound())
    .ExcludeFromDescription();

app.Run();

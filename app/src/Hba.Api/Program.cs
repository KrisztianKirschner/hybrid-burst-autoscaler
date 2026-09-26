using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();              // /openapi/v1.json
    app.MapScalarApiReference();   // /scalar
}

app.MapControllers();

app.Run();

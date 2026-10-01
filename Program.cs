using Microsoft.EntityFrameworkCore;
using PanaszKezelo.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// 1. Kapcsolati sztring lekérése az appsettings.json fájlból
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30))));
// 2. DbContext regisztrálása MySQL-hez
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Alapértelmezett vezérlõk és Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Fejlesztõi környezetben Swagger felület bekapcsolása
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
using AutoMapper;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Polly;
using Serilog;
using Swashbuckle.AspNetCore.Filters;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using UserManagement.Application.DTOs;
using UserManagement.Application.Mappings;
using UserManagement.Application.Services;
using UserManagement.Application.Validators;
using UserManagement.Domain.Interfaces;
using UserManagement.Infrastructure.Data;
using UserManagement.Infrastructure.Repositories;
using UserManagementApi.Domain.Entities;

var builder = WebApplication.CreateBuilder(args);

//Configurar Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Host.UseSerilog();

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle

// Configurar servicios
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "User Management API", Version = "v1" });

    // Definir el esquema de seguridad para Bearer token
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "Please enter JWT with Bearer into field (e.g., Bearer <token>)",
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });

    // Aplicar el requisito de seguridad a los endpoints que lo necesiten
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] { }
        }
    });

    // Filtro para aplicar automáticamente el requisito de seguridad a endpoints con [Authorize]
    c.OperationFilter<SecurityRequirementsOperationFilter>();
});

// Configurar DbContext con ensamblado de migraciones
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        b => b.MigrationsAssembly("UserManagement.Infrastructure")));


// Polly para reintentos
builder.Services.AddResiliencePipeline("sql-retry", pipeline =>
{
    pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<SqlException>(),
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(2)
    });
});

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddAutoMapper(typeof(MappingProfile));
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<UserDTOValidator>();

// Configurar JWT
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    throw new InvalidOperationException("Jwt:Key is not configured in appsettings.json or is empty.");
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))

        };

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                Console.WriteLine("Authentication failed: " + context.Exception.Message);
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                Console.WriteLine("Token validated successfully.");
                return Task.CompletedTask;
            }
        };
    });

// Agregar autorización
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Endpoint para registrar usuario
app.MapPost("/api/users/register", async (
    UserDTO userDto,
    IValidator<UserDTO> validator,
    IUserService userService,
    IMapper mapper) =>
{
    var validationResult = await validator.ValidateAsync(userDto);
    if (!validationResult.IsValid)
    {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    var existingUser = await userService.GetUserByEmailAsync(userDto.Email);
    if (existingUser != null)
    {
        return Results.Conflict(new { Message = "User already exists." });
    }

    var user = mapper.Map<User>(userDto);
    user.PasswordHash = userDto.Password; // Asegurar que la contraseña se pase correctamente
    var createdUser = await userService.CreateUserAsync(user);
    return Results.Created($"/api/users/{createdUser.Id}", mapper.Map<UserDTO>(createdUser));
})
.WithName("RegisterUser")
.WithOpenApi();

// Endpoint para login
app.MapPost("/api/users/login", async (UserDTO userDto, IValidator<UserDTO> validator, IUserService userService, IConfiguration configuration) =>
{
    // Validar entrada
    var validationResult = await validator.ValidateAsync(userDto);
    if (!validationResult.IsValid)
    {
        return Results.ValidationProblem(validationResult.ToDictionary());
    }

    // Verificar si el usuario existe y la contraseña es correcta
    var user = await userService.GetUserByEmailAsync(userDto.Email);
    if (user == null || !BCrypt.Net.BCrypt.Verify(userDto.Password, user.PasswordHash))
    {
        return Results.Unauthorized();
    }

    // Generar token JWT
    var claims = new[]
    {
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Email, user.Email),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    };

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:Key"]));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer: configuration["Jwt:Issuer"],
        audience: configuration["Jwt:Audience"],
        claims: claims,
        expires: DateTime.Now.AddMinutes(30),
        signingCredentials: creds);

    return Results.Ok(new
    {
        Token = new JwtSecurityTokenHandler().WriteToken(token)
    });
})
.WithName("LoginUser")
.WithOpenApi();

// Endpoint protegido con JWT
app.MapGet("/api/users/protected", () =>
{
    return Results.Ok(new { Message = "This is a protected endpoint. now you are inside the endpoint" });
})
.RequireAuthorization()
.WithName("ProtectedEndpoint")
.WithOpenApi();

// Endpoint protegido para obtener todos los usuarios
app.MapGet("/api/users/all", async (IUserService userService, IMapper mapper) =>
{
    var users = await userService.GetAllUsersAsync();
    var usersDto = mapper.Map<List<UserDTO>>(users);
    return Results.Ok(new { Message = "List of all users.", Users = usersDto });
})
.RequireAuthorization()
.WithName("GetAllUsers")
.WithOpenApi();

Console.WriteLine($"Jwt:Key = {builder.Configuration["Jwt:Key"]}");
Console.WriteLine($"Jwt:Issuer = {builder.Configuration["Jwt:Issuer"]}");
Console.WriteLine($"Jwt:Audience = {builder.Configuration["Jwt:Audience"]}");

app.Run();


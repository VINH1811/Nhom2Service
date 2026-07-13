using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Nhom2Service.Consumers;
using Nhom2Service.Data;
using Nhom2Service.Services;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var cs = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Thiếu DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("HRCore", c => c.BaseAddress = new Uri(builder.Configuration["ServiceUrls:HrCore"]!.TrimEnd('/') + "/"));

var secret = builder.Configuration["JWT:Secret"] ?? throw new InvalidOperationException("Thiếu JWT Secret");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.RequireHttpsMetadata = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["JWT:ValidIssuer"],
        ValidateAudience = true,
        ValidAudience = builder.Configuration["JWT:ValidAudience"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        RoleClaimType = ClaimTypes.Role,
        NameClaimType = ClaimTypes.Name
    };
    o.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = context.Principal?.FindFirst("SecurityStamp")?.Value;
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(stamp)) { context.Fail("Token thiếu thông tin tài khoản."); return; }
            try
            {
                var factory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
                var client = factory.CreateClient("HRCore");
                client.DefaultRequestHeaders.Remove("X-Internal-Key");
                client.DefaultRequestHeaders.Add("X-Internal-Key", builder.Configuration["InternalApiKey"]);
                var response = await client.GetAsync($"api/internal/accounts/{Uri.EscapeDataString(userId)}/validate?securityStamp={Uri.EscapeDataString(stamp)}", context.HttpContext.RequestAborted);
                if (!response.IsSuccessStatusCode) context.Fail("Tài khoản đã bị khóa hoặc phiên đăng nhập hết hiệu lực.");
            }
            catch { context.Fail("Không xác minh được trạng thái tài khoản với HR Core."); }
        }
    };
});
builder.Services.AddAuthorization();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<EmployeeChangedConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        // CloudAMQP bắt buộc TLS (amqps, cổng 5671). Ưu tiên RabbitMQ:Uri.
        var rabbitUri = builder.Configuration["RabbitMQ:Uri"];
        if (!string.IsNullOrWhiteSpace(rabbitUri))
        {
            cfg.Host(new Uri(rabbitUri));
        }
        else
        {
            var rHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
            var useSsl = builder.Configuration.GetValue<bool?>("RabbitMQ:UseSsl")
                ?? rHost.Contains("cloudamqp.com", StringComparison.OrdinalIgnoreCase);
            cfg.Host(rHost, builder.Configuration["RabbitMQ:VirtualHost"] ?? "/", h =>
            {
                h.Username(builder.Configuration["RabbitMQ:Username"] ?? "guest");
                h.Password(builder.Configuration["RabbitMQ:Password"] ?? "guest");
                if (useSsl) h.UseSsl(s => { s.Protocol = System.Security.Authentication.SslProtocols.Tls12; s.ServerName = rHost; });
            });
        }
        cfg.ReceiveEndpoint("nhom2-employee-changed", e => e.ConfigureConsumer<EmployeeChangedConsumer>(ctx));
    });
});

builder.Services.AddScoped<HrCoreClient>();
builder.Services.AddScoped<AttendanceSummaryService>();
builder.Services.AddScoped<DbInitializer>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "WLPRO Attendance Service", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = Array.Empty<string>() });
});
builder.Services.AddCors(o => o.AddPolicy("Frontend", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseSwagger(); app.UseSwaggerUI(); app.UseCors("Frontend"); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "Nhom2Service", database = "Nhom2DB", utc = DateTime.UtcNow })).AllowAnonymous();
using (var scope = app.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<DbInitializer>().InitializeAsync();
app.Run();

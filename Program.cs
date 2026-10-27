using RecipeCost.Pricing.Application;
using RecipeCost.Pricing.Integrations;
using RecipeCost.Pricing.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PricingCalculator>();
builder.Services.AddHttpClient<UsdaFoodDataClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["USDA_FDC_BASE_URL"] ?? "https://api.nal.usda.gov/fdc/v1/");
    client.Timeout = TimeSpan.FromSeconds(12);
});

var app = builder.Build();

app.MapGrpcService<PricingGrpcService>();
app.MapPricingServiceHttpApi();
app.MapGet("/", () => Results.Ok(new { service = "recipe-cost-pricing" }));

app.Run();

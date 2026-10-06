using System.Text.Json.Serialization;

namespace RecipeCost.Pricing.Application;

public sealed record IngredientProduct(
    string ProductId,
    string IngredientId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal PackageQuantity,
    int PackageUnitId);

public sealed record ProductPrice(
    string PriceId,
    string ProductId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Price,
    string CurrencyCode);

public sealed record IngredientPricing(IngredientProduct Product, ProductPrice Price);

public sealed record RecipeIngredient(
    string IngredientId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal Quantity,
    int UnitId);

public sealed record UnitConversion(
    string IngredientId,
    int FromUnitId,
    int ToUnitId,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal ConversionFactor);

public sealed record IngredientCost(
    string IngredientId,
    string ProductId,
    string PriceId,
    decimal Quantity,
    int UnitId,
    decimal Cost,
    string CurrencyCode);

public sealed record RecipeCost(
    decimal TotalCost,
    string? CurrencyCode,
    IReadOnlyList<IngredientCost> Ingredients);

public sealed class PricingCalculator
{
    public IngredientCost CalculateIngredientCost(
        RecipeIngredient ingredient,
        IngredientProduct product,
        ProductPrice price,
        IEnumerable<UnitConversion> conversions)
    {
        ArgumentNullException.ThrowIfNull(ingredient);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(conversions);

        ValidateQuantity(ingredient.Quantity, nameof(ingredient));
        ValidateQuantity(product.PackageQuantity, nameof(product), allowZero: false);
        ValidateQuantity(price.Price, nameof(price));

        if (!string.Equals(ingredient.IngredientId, product.IngredientId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The product does not belong to the requested ingredient.", nameof(product));
        }

        if (!string.Equals(product.ProductId, price.ProductId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The price does not belong to the selected product.", nameof(price));
        }

        if (string.IsNullOrWhiteSpace(price.CurrencyCode))
        {
            throw new ArgumentException("The price must include a currency code.", nameof(price));
        }

        var quantityInPackageUnit = ConvertQuantity(
            ingredient.IngredientId,
            ingredient.Quantity,
            ingredient.UnitId,
            product.PackageUnitId,
            conversions);

        var cost = quantityInPackageUnit / product.PackageQuantity * price.Price;
        return new IngredientCost(
            ingredient.IngredientId,
            product.ProductId,
            price.PriceId,
            ingredient.Quantity,
            ingredient.UnitId,
            cost,
            price.CurrencyCode);
    }

    public RecipeCost CalculateRecipeCost(
        IEnumerable<RecipeIngredient> ingredients,
        IEnumerable<IngredientPricing> pricing,
        IEnumerable<UnitConversion> conversions)
    {
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(conversions);

        var pricingByIngredient = pricing.ToDictionary(
            selection => selection.Product.IngredientId,
            StringComparer.Ordinal);
        var conversionList = conversions.ToArray();

        var ingredientCosts = ingredients
            .Select(ingredient =>
            {
                if (!pricingByIngredient.TryGetValue(ingredient.IngredientId, out var selection))
                {
                    throw new InvalidOperationException(
                        $"No selected product price was found for ingredient '{ingredient.IngredientId}'.");
                }

                return CalculateIngredientCost(ingredient, selection.Product, selection.Price, conversionList);
            })
            .ToArray();

        var currencyCodes = ingredientCosts
            .Select(item => item.CurrencyCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (currencyCodes.Length > 1)
        {
            throw new InvalidOperationException("A recipe cannot total prices in different currencies.");
        }

        return new RecipeCost(
            ingredientCosts.Sum(item => item.Cost),
            currencyCodes.FirstOrDefault(),
            ingredientCosts);
    }

    private static decimal ConvertQuantity(
        string ingredientId,
        decimal quantity,
        int fromUnitId,
        int toUnitId,
        IEnumerable<UnitConversion> conversions)
    {
        if (fromUnitId == toUnitId)
        {
            return quantity;
        }

        var conversionList = conversions
            .Where(conversion => string.Equals(
                conversion.IngredientId,
                ingredientId,
                StringComparison.Ordinal))
            .ToArray();

        var factors = new Dictionary<int, decimal>
        {
            [fromUnitId] = 1m
        };
        var pendingUnits = new Queue<int>();
        pendingUnits.Enqueue(fromUnitId);

        while (pendingUnits.TryDequeue(out var currentUnitId))
        {
            foreach (var conversion in conversionList)
            {
                ValidateQuantity(conversion.ConversionFactor, nameof(conversions), allowZero: false);

                if (conversion.FromUnitId == currentUnitId)
                {
                    AddConversion(conversion.ToUnitId, factors[currentUnitId] * conversion.ConversionFactor);
                }

                if (conversion.ToUnitId == currentUnitId)
                {
                    AddConversion(conversion.FromUnitId, factors[currentUnitId] / conversion.ConversionFactor);
                }
            }

            if (factors.TryGetValue(toUnitId, out var factor))
            {
                return quantity * factor;
            }
        }

        throw new InvalidOperationException(
            $"No unit conversion was found for ingredient '{ingredientId}' from unit '{fromUnitId}' to '{toUnitId}'.");

        void AddConversion(int unitId, decimal factor)
        {
            if (factors.TryAdd(unitId, factor))
            {
                pendingUnits.Enqueue(unitId);
            }
        }
    }

    private static void ValidateQuantity(decimal value, string parameterName, bool allowZero = true)
    {
        if (value < 0 || (!allowZero && value == 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Quantity and price values must be positive.");
        }
    }
}
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Recipes;

public static class RecipeServices
{
    /// <summary>
    /// Registers the recipes' activities that need services. The application registers what they use:
    /// <see cref="IApprovalMailer"/>, <see cref="IPaymentProvider"/>, <see cref="IPaymentRefunds"/>,
    /// <see cref="IStock"/>, <see cref="IFraudChecks"/> and <see cref="IShipping"/>.
    /// </summary>
    public static IServiceCollection AddWaitRecipes(this IServiceCollection services)
    {
        services.AddTransient<RequestApproval>();
        services.AddTransient<CreatePayment>();
        services.AddTransient<CheckFraud>();
        services.AddTransient<RefundPayment>();
        services.AddTransient<ReserveStock>();
        services.AddTransient<Ship>();
        services.AddTransient<CancelFlaggedOrder>();
        return services;
    }
}

using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

///// <summary>
///// Builder for defining saga workflows with compensation and error handling capabilities
///// </summary>
//public interface ISagaBuilder<TWorkflowData, TPreviousStepData> 
//    where TPreviousStepData : class
//{
//    /// <summary>
//    /// Adds a step to the saga execution
//    /// </summary>
//    /// <typeparam name="TActivity">The activity type for this step</typeparam>
//    /// <param name="configure">Configuration for the saga activity</param>
//    ISagaActivityBuilder<TWorkflowData, TActivity> Step<TActivity>(
//        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure)
//        where TActivity : class, IAsyncActivity;
//}

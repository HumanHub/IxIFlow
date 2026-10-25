namespace IxIFlow.Generators.Tests;

public sealed partial class ActivityCompilationTests
{
    [Trait("Feature", "authoring")]
    [Trait("Spec", "F-19")]
    public sealed class Integral8Bit
    {
        [Theory]
        [MemberData(nameof(IntegralTypesOfWidth), 1, MemberType = typeof(ActivityCompilationTests))]
        public void IntegralDefaultsConvertExactlyWithoutChangingAuthoredNumbers(string type, Type runtime) =>
            ActivityCompilationTests.IntegralDefaultsConvertExactlyWithoutChangingAuthoredNumbers(type, runtime);

        [Theory]
        [MemberData(nameof(IntegralTypesOfWidth), 1, MemberType = typeof(ActivityCompilationTests))]
        public void FractionalAndOutOfRangeIntegralDefaultsAreRefused(string type, Type runtime) =>
            ActivityCompilationTests.FractionalAndOutOfRangeIntegralDefaultsAreRefused(type, runtime);
    }
}

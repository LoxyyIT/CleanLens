using CleanLens.Core.Models;

namespace CleanLens.Core.Safety;

public static class ConfidenceScorer
{
    public static ConfidenceLevel Score(int independentSignals, bool exactProductIdentity, bool isUserData)
    {
        if (isUserData || independentSignals < 2)
        {
            return ConfidenceLevel.Low;
        }

        if (exactProductIdentity && independentSignals >= 3)
        {
            return ConfidenceLevel.High;
        }

        return ConfidenceLevel.Medium;
    }

    public static bool SelectByDefault(ConfidenceLevel confidence, bool isUserData) => confidence == ConfidenceLevel.High && !isUserData;
}

namespace Apocasaver
{
    internal static class CleanPolicy
    {
        internal const float Distance = 1000f, Seconds = 600f;
        internal static bool Eligible(bool known, bool protectedObject, bool approvedCategory, float distance, float farSeconds)
        { return known && !protectedObject && approvedCategory && distance > Distance && farSeconds >= Seconds; }
    }
}

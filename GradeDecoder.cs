namespace SinglesSlinger
{
    /// <summary>
    /// Decodes the encoded <c>cardGrade</c> integer introduced by the
    /// TCGCardShopSimulator.GradingOverhaul mod.
    /// <para>
    /// Encoding scheme (from Helper.cs in the overhaul mod):
    /// <list type="bullet">
    ///   <item>Cardinals vanilla: raw 1–10 (no encoding)</item>
    ///   <item>Cardinals with cert: 0–199,999,999 range</item>
    ///   <item>PSA: 200,000,000 base</item>
    ///   <item>Beckett: 300,000,000 base</item>
    ///   <item>Cheat flag: +1,000,000,000</item>
    /// </list>
    /// Each company encodes as: base + (gradeSlot × 10,000,000) + certNumber.
    /// </para>
    /// </summary>
    internal static class GradeDecoder
    {
        /// <summary>
        /// Decodes an encoded <paramref name="cardGrade"/> into its actual numeric grade
        /// (1–10) and the name of the grading company.
        /// </summary>
        /// <param name="cardGrade">The raw encoded grade integer from <c>CardData.cardGrade</c>.</param>
        /// <param name="grade">The decoded numeric grade (1–10).</param>
        /// <param name="company">The grading company name: "Cardinals", "PSA", or "Beckett".</param>
        internal static void DecodeCardGrade(int cardGrade, out int grade, out string company)
        {
            int num = cardGrade;

            // Strip cheat flag if present
            if (num >= 1000000000)
                num -= 1000000000;

            // Vanilla Cardinals (simple 1–10, no cert)
            if (num >= 1 && num <= 10)
            {
                company = "Cardinals";
                grade = num;
                return;
            }

            // Beckett (300,000,000 base) – check before PSA since it is the higher base
            if (num >= 300000000)
            {
                company = "Beckett";
                int relative = num - 300000000;
                int slot = relative / 10000000;
                grade = SlotToGrade(slot);
                return;
            }

            // PSA (200,000,000 base)
            if (num >= 200000000)
            {
                company = "PSA";
                int relative = num - 200000000;
                int slot = relative / 10000000;
                grade = SlotToGrade(slot);
                return;
            }

            // Cardinals with cert (new style, same base but has cert encoded)
            company = "Cardinals";
            int slot2 = num / 10000000;
            grade = SlotToGrade(slot2);
        }

        /// <summary>
        /// Converts a zero-based grade slot (0–9) to a 1-based grade (1–10).
        /// Returns 1 for out-of-range slots.
        /// </summary>
        private static int SlotToGrade(int slot)
        {
            if (slot < 0 || slot > 9) return 1;
            return slot + 1;
        }
    }
}
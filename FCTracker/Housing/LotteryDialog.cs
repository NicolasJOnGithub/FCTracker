namespace FCTracker.Housing;

using System;
using System.Linq;
using System.Text;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

/// <summary>
/// Reading and classifying the placard's confirmation dialogs.
///
/// This is the dangerous part of the automation: accepting a refund and buying a plot are both
/// SelectYesno prompts, and firing Yes at the wrong one spends the FC's gil irreversibly. So the
/// classifier is deliberately one-sided - it will only ever report <see cref="DialogKind.Refund"/>
/// when the text positively says refund AND says nothing about purchasing or claiming. Everything
/// it is unsure about comes back as <see cref="DialogKind.Other"/>, which the runner declines.
/// </summary>
public static class LotteryDialog
{
    public enum DialogKind
    {
        /// <summary>Not a lottery result prompt, or not recognised. Never auto-confirmed.</summary>
        Other,

        /// <summary>Addon 7124 (lost, full refund) or 7123 (forfeited win, 50% refund).</summary>
        Refund,

        /// <summary>Addon 7122 / 7128 - won, offering to finalise the purchase. Never auto-confirmed.</summary>
        ClaimPlot,
    }

    // Addon 7124: "...May you have better luck in the future! Accept a full refund of your deposit of N gil?"
    // Addon 7123: "...Accept the remaining refund of N gil?"
    private static readonly string[] RefundWording =
    [
        "accept a full refund",
        "accept the remaining refund",
        "better luck in the future",
    ];

    // Addon 7122 / 7128: "...Finalize your purchase and claim your plot of land?"
    // Addon 7046: the entry confirmation. None of these may ever be auto-confirmed.
    private static readonly string[] PurchaseWording =
    [
        "finalize your purchase",
        "claim your plot",
        "congratulations",
        "wish to deposit",
        "enter the lottery",
    ];

    public static DialogKind Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DialogKind.Other;

        string lower = text.ToLowerInvariant();

        // Purchase wording wins outright. A prompt that mentions both is not one we understand,
        // and the safe reading of "not understood" is "do not touch it".
        if (PurchaseWording.Any(lower.Contains))
            return DialogKind.ClaimPlot;

        return RefundWording.Any(lower.Contains) ? DialogKind.Refund : DialogKind.Other;
    }

    /// <summary>Gil amount named in the prompt, or 0. Used to log what a refund was worth.</summary>
    public static long ReadAmount(string text)
    {
        System.Text.RegularExpressions.Match match = RegexHelper.GilAmountRegex().Match(text);
        if (!match.Success)
            return 0;

        string digits = new(match.Groups[1].Value.Where(char.IsDigit).ToArray());

        return long.TryParse(digits, out long value) ? value : 0;
    }

    public static unsafe string ReadText(AtkUnitBase* addon)
    {
        if (addon == null)
            return string.Empty;

        StringBuilder sb = new();

        for (int i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            AtkResNode* node = addon->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Text)
                continue;

            string value = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                sb.Append(value).Append(' ');
        }

        return sb.ToString();
    }

    public static void LogClassification(string addonName, string text, DialogKind kind) =>
        Svc.Log.Debug($"[FCTracker lottery] {addonName} classified {kind}: {Truncate(text, 240)}");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

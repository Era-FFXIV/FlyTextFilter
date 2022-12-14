﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Logging;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FlyTextFilter.Model;
using FlyTextFilter.Model.FlyTextAdjustments;

namespace FlyTextFilter;

public unsafe class FlyTextHandler
{
    public bool ShouldLog;
    public bool HasLoadingFailed;
    public bool? HasPositionTestFailed;
    public bool? HasScalingTestFailed;
    public ConcurrentQueue<FlyTextLog> Logs = new();

    private readonly delegate* unmanaged<long, long> getTargetIdDelegate; // BattleChara_vf84 in 6.2
    private int? val1Preview;

    private delegate void* AddonFlyTextOnSetupDelegate(
        void* a1,
        void* a2,
        void* a3);
    private readonly Hook<AddonFlyTextOnSetupDelegate>? addonFlyTextOnSetupHook;

    private delegate void AddToScreenLogWithScreenLogKindDelegate(
        Character* target,
        Character* source,
        FlyTextKind logKind,
        byte option,
        byte actionKind,
        int actionId,
        int val1,
        int val2,
        int val3);
    private readonly Hook<AddToScreenLogWithScreenLogKindDelegate>? addToScreenLogWithScreenLogKindHook;

    private delegate void* AddToScreenLogDelegate(
        long targetId,
        FlyTextCreation* flyTextCreation);
    private readonly Hook<AddToScreenLogDelegate>? addToScreenLogHook;

    public FlyTextHandler()
    {
        IntPtr addonFlyTextOnSetupAddress;
        IntPtr getTargetIdAddress;
        IntPtr addToScreenLogWithScreenLogKindAddress;
        IntPtr addToScreenLogAddress;

        try
        {
            addonFlyTextOnSetupAddress = Service.SigScanner.ScanText("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 20 80 89");
            getTargetIdAddress = Service.SigScanner.ScanText("48 8D 81 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC 48 8D 81 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC 48 8D 81 ?? ?? ?? ?? C3 CC CC CC CC CC CC CC CC 48 89 5C 24 ?? 48 89 74 24");
            addToScreenLogWithScreenLogKindAddress = Service.SigScanner.ScanText("E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 3A");
            addToScreenLogAddress = Service.SigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 4C 24 ?? 48 33 CC E8 ?? ?? ?? ?? 48 83 C4 68 41 5F 41 5E");
        }
        catch (Exception ex)
        {
            this.HasLoadingFailed = true;
            PluginLog.Error(ex, "Sig scan failed.");
            return;
        }

        Service.FlyTextGui.FlyTextCreated += this.FlyTextCreate;

        this.getTargetIdDelegate = (delegate* unmanaged<long, long>)getTargetIdAddress;

        this.addonFlyTextOnSetupHook = Hook<AddonFlyTextOnSetupDelegate>.FromAddress(addonFlyTextOnSetupAddress, this.AddonFlyTextOnSetupDetour);
        this.addonFlyTextOnSetupHook.Enable();

        this.addToScreenLogWithScreenLogKindHook = Hook<AddToScreenLogWithScreenLogKindDelegate>.FromAddress(addToScreenLogWithScreenLogKindAddress, this.AddToScreenLogWithScreenLogKindDetour);
        this.addToScreenLogWithScreenLogKindHook.Enable();

        this.addToScreenLogHook = Hook<AddToScreenLogDelegate>.FromAddress(addToScreenLogAddress, this.AddToScreenLogDetour);
        this.addToScreenLogHook.Enable();

        this.ApplyPositions();
        this.ApplyScaling();
    }

    public void ResetPositions()
    {
        var defaultFlyTextPositions = FlyTextPositions.GetDefaultPositions();
        this.SetPositions(defaultFlyTextPositions);
    }

    public void ApplyPositions()
    {
        var flyTextPositions = Service.Configuration.FlyTextAdjustments.FlyTextPositions;
        this.SetPositions(flyTextPositions);
    }

    public void SetPositions(FlyTextPositions flyTextPositions)
    {
        var addon = Service.GameGui.GetAddonByName("_FlyText", 1);
        if (addon == IntPtr.Zero || this.HasLoadingFailed)
        {
            return;
        }

        var flyTextArray = (FlyTextArray*)(addon + 0x2710); // AddonFlyText_Initialize

        if (this.HasPositionTestFailed == null)
        {
            var defaultFlyTextPositions = FlyTextPositions.GetDefaultPositions();
            this.HasPositionTestFailed = false;
            this.HasPositionTestFailed |= Math.Abs((*flyTextArray)[0]->X - defaultFlyTextPositions.HealingGroupX!.Value) > 0.01f;
            this.HasPositionTestFailed |= Math.Abs((*flyTextArray)[0]->Y - defaultFlyTextPositions.HealingGroupY!.Value) > 0.01f;
            this.HasPositionTestFailed |= Math.Abs((*flyTextArray)[1]->X - defaultFlyTextPositions.StatusDamageGroupX!.Value) > 0.01f;
            this.HasPositionTestFailed |= Math.Abs((*flyTextArray)[1]->Y - defaultFlyTextPositions.StatusDamageGroupY!.Value) > 0.01f;

            if (this.HasPositionTestFailed!.Value)
            {
                PluginLog.Error("Position test failed.");
                this.Dispose();
                this.HasLoadingFailed = true;
                return;
            }
        }

        if (flyTextPositions.HealingGroupX != null)
        {
            (*flyTextArray)[0]->X = flyTextPositions.HealingGroupX.Value;
        }

        if (flyTextPositions.HealingGroupY != null)
        {
            (*flyTextArray)[0]->Y = flyTextPositions.HealingGroupY.Value;
        }

        if (flyTextPositions.StatusDamageGroupX != null)
        {
            (*flyTextArray)[1]->X = flyTextPositions.StatusDamageGroupX.Value;
        }

        if (flyTextPositions.StatusDamageGroupY != null)
        {
            (*flyTextArray)[1]->Y = flyTextPositions.StatusDamageGroupY.Value;
        }
    }

    public void ResetScaling()
    {
        this.SetScaling(1.0f, 1.0f);
    }

    public void ApplyScaling()
    {
        var adjustmentsConfig = Service.Configuration.FlyTextAdjustments;
        this.SetScaling(adjustmentsConfig.FlyTextScale, adjustmentsConfig.PopupTextScale);
    }

    public void SetScaling(float? flyTextScale, float? popUpTextScale)
    {
        var agent = (IntPtr)Framework.Instance()->GetUiModule()->GetAgentModule()->GetAgentByInternalId(AgentId.ScreenLog);
        if (agent == IntPtr.Zero || this.HasLoadingFailed)
        {
            return;
        }

        var currFlyTextScale = (float*)(agent + 0x4C);
        var currPopUpTextScale = (float*)(agent + 0x344);

        if (this.HasScalingTestFailed == null)
        {
            this.HasScalingTestFailed = false;
            this.HasScalingTestFailed |= Math.Abs(*currFlyTextScale - 1.0f) > 0.01f;
            this.HasScalingTestFailed |= Math.Abs(*currPopUpTextScale - 1.0f) > 0.01f;

            if (this.HasScalingTestFailed!.Value)
            {
                PluginLog.Error("Scaling test failed.");
                this.Dispose();
                this.HasLoadingFailed = true;
                return;
            }
        }

        if (flyTextScale != null)
        {
            *currFlyTextScale = flyTextScale.Value;
        }

        if (popUpTextScale != null)
        {
            *currPopUpTextScale = popUpTextScale.Value;
        }
    }

    public void CreateFlyText(FlyTextKind flyTextKind, byte sourceStyle, byte targetStyle)
    {
        var localPlayer = Service.ClientState.LocalPlayer?.Address.ToInt64();
        if (localPlayer != null)
        {
            var targetId = this.getTargetIdDelegate(localPlayer.Value);
            int val1;
            if (flyTextKind is FlyTextKind.NamedIcon2 or FlyTextKind.NamedIconFaded2)
                val1 = 3166;
            else if (flyTextKind is FlyTextKind.NamedIcon or FlyTextKind.NamedIconFaded)
                val1 = 3260;
            else
                val1 = 1111;

            var val2 = 0;
            if (flyTextKind is FlyTextKind.Exp or FlyTextKind.IslandExp)
            {
                val2 = 10;
            }

            var actionId = flyTextKind is FlyTextKind.NamedAttack2 or FlyTextKind.NamedCriticalHit2 ? 16230 : 2555;

            var flyTextCreation = new FlyTextCreation
            {
                FlyTextKind = flyTextKind,
                SourceStyle = sourceStyle,
                TargetStyle = targetStyle,
                Option = 5,
                ActionKind = (byte)(flyTextKind == FlyTextKind.NamedIconWithItemOutline ? 2 : 1),
                ActionId = actionId,
                Val1 = val1,
                Val2 = val2,
                Val3 = 0,
            };

            this.val1Preview = flyTextCreation.Val1;

            this.addToScreenLogHook?.Original(targetId, &flyTextCreation);
        }
    }

    public void CreateFlyText(FlyTextLog flyTextLog)
    {
        var localPlayer = Service.ClientState.LocalPlayer?.Address.ToInt64();
        if (localPlayer != null)
        {
            var targetId = this.getTargetIdDelegate(localPlayer.Value);
            var flyTextCreation = flyTextLog.FlyTextCreation;
            this.val1Preview = flyTextCreation.Val1;
            this.addToScreenLogHook?.Original(targetId, &flyTextCreation);
        }
    }

    public void Dispose()
    {
        Service.FlyTextGui.FlyTextCreated -= this.FlyTextCreate;
        this.addonFlyTextOnSetupHook?.Dispose();
        this.addToScreenLogHook?.Dispose();
        this.addToScreenLogWithScreenLogKindHook?.Dispose();
        this.ResetPositions();
        this.ResetScaling();
    }

    private static bool ShouldFilter(Character* source, Character* target, FlyTextKind flyTextKind)
    {
        if (source != null
            && target != null &&
            Service.Configuration.FlyTextSettings.TryGetValue(flyTextKind, out var flyTextSetting))
        {
            switch (GetFlyTextCharCategory(source))
            {
                case FlyTextCharCategory.You:
                    return ShouldFilter(target, flyTextSetting.SourceYou);
                case FlyTextCharCategory.Party:
                    return ShouldFilter(target, flyTextSetting.SourceParty);
                case FlyTextCharCategory.Others:
                    return ShouldFilter(target, flyTextSetting.SourceOthers);
                case FlyTextCharCategory.None:
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool ShouldFilter(Character* target, FlyTextTargets flyTextTargets)
    {
        switch (GetFlyTextCharCategory(target))
        {
            case FlyTextCharCategory.You:
                return flyTextTargets.HasFlag(FlyTextTargets.You);
            case FlyTextCharCategory.Party:
                return flyTextTargets.HasFlag(FlyTextTargets.Party);
            case FlyTextCharCategory.Others:
                return flyTextTargets.HasFlag(FlyTextTargets.Others);
            case FlyTextCharCategory.None:
            default:
                return false;
        }
    }

    private static FlyTextCharCategory GetFlyTextCharCategory(Character* character)
    {
        var localPlayer = (Character*)(Service.ClientState.LocalPlayer?.Address ?? IntPtr.Zero);
        if (character == null || localPlayer == null)
        {
            return FlyTextCharCategory.None;
        }

        if (character == localPlayer)
        {
            return FlyTextCharCategory.You;
        }

        if (Util.IsPartyMember(character))
        {
            return FlyTextCharCategory.Party;
        }

        return FlyTextCharCategory.Others;
    }

    private void FlyTextCreate(
        ref FlyTextKind flyTextKind,
        ref int val1,
        ref int val2,
        ref SeString text1,
        ref SeString text2,
        ref uint color,
        ref uint icon,
        ref float yOffset,
        ref bool handled)
    {
        if (!Service.Configuration.Blacklist.Any())
        {
            return;
        }

        // preview
        if (this.val1Preview != null && val1 == this.val1Preview)
        {
            this.val1Preview = null;
            return;
        }

        var text1Adjusted = text1.ToString();

        // status effects
        if (text1.TextValue.StartsWith("+ ") || text1.TextValue.StartsWith("- "))
        {
            text1Adjusted = text1.TextValue[2..];
        }

        if (Service.Configuration.Blacklist.Contains(text1Adjusted)
            || Service.Configuration.Blacklist.Contains(text2.TextValue))
        {
            handled = true;
            if (this.ShouldLog)
            {
                var last = this.Logs.LastOrDefault();
                if (last != null && last.FlyTextCreation.FlyTextKind == flyTextKind && last.FlyTextCreation.Val1 == val1)
                {
                    last.WasFiltered = true;
                }
            }
        }
    }

    private void AddLog(FlyTextLog flyTextLog)
    {
        this.Logs.Enqueue(flyTextLog);

        while (this.Logs.Count > Service.Configuration.NbOfLogs)
        {
            this.Logs.TryDequeue(out _);
        }
    }

    private void* AddonFlyTextOnSetupDetour(void* a1, void* a2, void* a3)
    {
        var result = this.addonFlyTextOnSetupHook!.Original(a1, a2, a3);
        try
        {
            this.ApplyPositions();
            this.ApplyScaling();
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Exception in AddonFlyTextOnSetupDetour");
        }

        return result;
    }

    private void AddToScreenLogWithScreenLogKindDetour(
        Character* target,
        Character* source,
        FlyTextKind flyTextKind,
        byte option, // 0 = DoT / 1 = % increase / 2 = blocked / 3 = parried / 4 = resisted / 5 = default
        byte actionKind,
        int actionId,
        int val1,
        int val2,
        int val3)
    {
        try
        {
            var adjustedSource = source;
            if ((Service.Configuration.ShouldAdjustDotSource && flyTextKind == FlyTextKind.AutoAttack && option == 0 && actionKind == 0 && target == source)
                || (Service.Configuration.ShouldAdjustPetSource && source->GameObject.SubKind == (int)BattleNpcSubKind.Pet && source->CompanionOwnerID == Service.ClientState.LocalPlayer?.ObjectId)
                || (Service.Configuration.ShouldAdjustChocoboSource && source->GameObject.SubKind == (int)BattleNpcSubKind.Chocobo && source->CompanionOwnerID == Service.ClientState.LocalPlayer?.ObjectId))
            {
                adjustedSource = (Character*)(Service.ClientState.LocalPlayer?.Address ?? IntPtr.Zero);
                if (adjustedSource == null)
                {
                    adjustedSource = source;
                }
            }

            var shouldFilter = ShouldFilter(adjustedSource, target, flyTextKind);

            if (this.ShouldLog)
            {
                var flyTextLog = new FlyTextLog
                {
                    SourceCategory = GetFlyTextCharCategory(adjustedSource),
                    TargetCategory = GetFlyTextCharCategory(target),
                    HasSourceBeenAdjusted = source != adjustedSource,
                    WasFiltered = shouldFilter,
                    IsPartial = true,
                };

                this.AddLog(flyTextLog);
                this.addToScreenLogWithScreenLogKindHook!.Original(target, source, flyTextKind, (byte)(option + (shouldFilter ? 150 : 100)), actionKind, actionId, val1, val2, val3);
                return;
            }

            if (shouldFilter)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Exception in AddScreenLogDetour");
        }

        this.addToScreenLogWithScreenLogKindHook!.Original(target, source, flyTextKind, option, actionKind, actionId, val1, val2, val3);
    }

    private void* AddToScreenLogDetour(long targetId, FlyTextCreation* flyTextCreation)
    {
        try
        {
            bool shouldFilter;

            // classic function
            if (flyTextCreation->Option >= 100)
            {
                // should filter
                if (flyTextCreation->Option >= 150)
                {
                    flyTextCreation->Option -= 150;
                    shouldFilter = true;
                }
                else
                {
                    flyTextCreation->Option -= 100;
                    shouldFilter = false;
                }

                if (this.ShouldLog)
                {
                    foreach (var flyTextLog in this.Logs)
                    {
                        if (flyTextLog.IsPartial)
                        {
                            flyTextLog.FlyTextCreation = *flyTextCreation;
                            flyTextLog.IsPartial = false;
                            break;
                        }
                    }
                }
            }
            else
            {
                // item or crafting function
                var localPlayer = (Character*)(Service.ClientState.LocalPlayer?.Address ?? IntPtr.Zero);
                shouldFilter = ShouldFilter(localPlayer, localPlayer, flyTextCreation->FlyTextKind);

                if (this.ShouldLog)
                {
                    var flyTextLog = new FlyTextLog
                    {
                        FlyTextCreation = *flyTextCreation,
                        SourceCategory = FlyTextCharCategory.You,
                        TargetCategory = FlyTextCharCategory.You,
                        WasFiltered = shouldFilter,
                    };

                    this.AddLog(flyTextLog);
                }
            }

            if (shouldFilter)
            {
                return (void*)0;
            }
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Exception in AddToScreenLogDetour");
        }

        return this.addToScreenLogHook!.Original(targetId, flyTextCreation);
    }
}

using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.Chat.Managers;
using Content.Server.DeviceNetwork;
using Content.Server.DeviceNetwork.Components;
using Content.Server.DeviceNetwork.Systems;
using Content.Server.Labels;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.SS220.Photocopier;
using Content.Server.Tools;
using Content.Shared.UserInterface;
using Content.Shared.Administration.Logs;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Database;
using Content.Shared.DeviceNetwork;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Fax;
using Content.Shared.Fax.Systems;
using Content.Shared.Fax.Components;
using Content.Shared.Interaction;
using Content.Shared.Labels.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Paper;
using Content.Shared.SS220.Photocopier;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Content.Shared.NameModifier.Components;
using Content.Shared.Popups;
using Content.Shared.Power;

namespace Content.Server.Fax;

public sealed class FaxSystem : EntitySystem
{
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly IAdminManager _adminManager = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlotsSystem = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private readonly PopupSystem _popupSystem = default!;
    [Dependency] private readonly DeviceNetworkSystem _deviceNetworkSystem = default!;
    [Dependency] private readonly PaperSystem _paperSystem = default!;
    [Dependency] private readonly LabelSystem _labelSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly ToolSystem _toolSystem = default!;
    [Dependency] private readonly QuickDialogSystem _quickDialog = default!;
    [Dependency] private readonly UserInterfaceSystem _userInterface = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly PhotocopierSystem _photocopierSystem = default!;
    [Dependency] private readonly FaxecuteSystem _faxecute = default!;
    [Dependency] private readonly EmagSystem _emag = default!;

    private const string PaperSlotId = "Paper";

    public override void Initialize()
    {
        base.Initialize();

        // Hooks
        SubscribeLocalEvent<FaxMachineComponent, ComponentInit>(OnComponentInit);
        SubscribeLocalEvent<FaxMachineComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<FaxMachineComponent, ComponentRemove>(OnComponentRemove);

        SubscribeLocalEvent<FaxMachineComponent, EntInsertedIntoContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<FaxMachineComponent, EntRemovedFromContainerMessage>(OnItemSlotChanged);
        SubscribeLocalEvent<FaxMachineComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<FaxMachineComponent, DeviceNetworkPacketEvent>(OnPacketReceived);

        // Interaction
        SubscribeLocalEvent<FaxMachineComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<FaxMachineComponent, GotEmaggedEvent>(OnEmagged);

        // UI
        SubscribeLocalEvent<FaxMachineComponent, AfterActivatableUIOpenEvent>(OnToggleInterface);
        SubscribeLocalEvent<FaxMachineComponent, FaxSendMessage>(OnSendButtonPressed);
        SubscribeLocalEvent<FaxMachineComponent, FaxRefreshMessage>(OnRefreshButtonPressed);
        SubscribeLocalEvent<FaxMachineComponent, FaxDestinationMessage>(OnDestinationSelected);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<FaxMachineComponent, ApcPowerReceiverComponent>();
        while (query.MoveNext(out var uid, out var fax, out var receiver))
        {
            if (!receiver.Powered)
                continue;

            ProcessPrintingAnimation(uid, frameTime, fax);
            ProcessInsertingAnimation(uid, frameTime, fax);
            ProcessSendingTimeout(uid, frameTime, fax);
        }
    }

    private void ProcessPrintingAnimation(EntityUid uid, float frameTime, FaxMachineComponent comp)
    {
        if (comp.PrintingTimeRemaining > 0)
        {
            comp.PrintingTimeRemaining -= frameTime;
            UpdateAppearance(uid, comp);

            var isAnimationEnd = comp.PrintingTimeRemaining <= 0;
            if (isAnimationEnd)
            {
                SpawnPaperFromQueue(uid, comp);
                UpdateUserInterface(uid, comp);
            }

            return;
        }

        if (comp.PrintingQueue.Count > 0)
        {
            comp.PrintingTimeRemaining = comp.PrintingTime;
            _audioSystem.PlayPvs(comp.PrintSound, uid);
        }
    }

    private void ProcessInsertingAnimation(EntityUid uid, float frameTime, FaxMachineComponent comp)
    {
        if (comp.InsertingTimeRemaining <= 0)
            return;

        comp.InsertingTimeRemaining -= frameTime;
        UpdateAppearance(uid, comp);

        var isAnimationEnd = comp.InsertingTimeRemaining <= 0;
        if (isAnimationEnd)
        {
            _itemSlotsSystem.SetLock(uid, comp.PaperSlot, false);
            UpdateUserInterface(uid, comp);
        }
    }

    private void ProcessSendingTimeout(EntityUid uid, float frameTime, FaxMachineComponent comp)
    {
        if (comp.SendTimeoutRemaining > 0)
        {
            comp.SendTimeoutRemaining -= frameTime;

            if (comp.SendTimeoutRemaining <= 0)
                UpdateUserInterface(uid, comp);
        }
    }

    private void OnComponentInit(EntityUid uid, FaxMachineComponent component, ComponentInit args)
    {
        _itemSlotsSystem.AddItemSlot(uid, PaperSlotId, component.PaperSlot);
        UpdateAppearance(uid, component);
    }

    private void OnComponentRemove(EntityUid uid, FaxMachineComponent component, ComponentRemove args)
    {
        _itemSlotsSystem.RemoveItemSlot(uid, component.PaperSlot);
    }

    private void OnMapInit(EntityUid uid, FaxMachineComponent component, MapInitEvent args)
    {
        // Load all faxes on map in cache each other to prevent taking same name by user created fax
        Refresh(uid, component);
    }

    private void OnItemSlotChanged(EntityUid uid, FaxMachineComponent component, ContainerModifiedMessage args)
    {
        if (!component.Initialized)
            return;

        if (args.Container.ID != component.PaperSlot.ID)
            return;

        var isPaperInserted = component.PaperSlot.Item.HasValue;
        if (isPaperInserted)
        {
            component.InsertingTimeRemaining = component.InsertionTime;
            _itemSlotsSystem.SetLock(uid, component.PaperSlot, true);
        }

        UpdateUserInterface(uid, component);
    }

    private void OnPowerChanged(EntityUid uid, FaxMachineComponent component, ref PowerChangedEvent args)
    {
        var isInsertInterrupted = !args.Powered && component.InsertingTimeRemaining > 0;
        if (isInsertInterrupted)
        {
            component.InsertingTimeRemaining = 0f; // Reset animation

            // Drop from slot because animation did not play completely
            _itemSlotsSystem.SetLock(uid, component.PaperSlot, false);
            _itemSlotsSystem.TryEject(uid, component.PaperSlot, null, out var _, true);
        }

        var isPrintInterrupted = !args.Powered && component.PrintingTimeRemaining > 0;
        if (isPrintInterrupted)
        {
            component.PrintingTimeRemaining = 0f; // Reset animation
        }

        if (isInsertInterrupted || isPrintInterrupted)
            UpdateAppearance(uid, component);

        _itemSlotsSystem.SetLock(uid, component.PaperSlot, !args.Powered); // Lock slot when power is off
    }

    private void OnInteractUsing(EntityUid uid, FaxMachineComponent component, InteractUsingEvent args)
    {
        if (args.Handled ||
            !TryComp<ActorComponent>(args.User, out var actor) ||
            !_toolSystem.HasQuality(args.Used, "Screwing")) // Screwing because Pulsing already used by device linking
            return;

        _quickDialog.OpenDialog(actor.PlayerSession,
            Loc.GetString("fax-machine-dialog-rename"),
            Loc.GetString("fax-machine-dialog-field-name"),
            (string newName) =>
        {
            if (component.FaxName == newName)
                return;

            if (newName.Length > 20)
            {
                _popupSystem.PopupEntity(Loc.GetString("fax-machine-popup-name-long"), uid);
                return;
            }

            if (component.KnownFaxes.ContainsValue(newName) && !_emag.CheckFlag(uid, EmagType.Interaction)) // Allow existing names if emagged for fun
            {
                _popupSystem.PopupEntity(Loc.GetString("fax-machine-popup-name-exist"), uid);
                return;
            }

            _adminLogger.Add(LogType.Action,
                LogImpact.Low,
                $"{ToPrettyString(args.User):user} renamed {ToPrettyString(uid):tool} from \"{component.FaxName}\" to \"{newName}\"");
            component.FaxName = newName;
            _popupSystem.PopupEntity(Loc.GetString("fax-machine-popup-name-set"), uid);
            UpdateUserInterface(uid, component);
        });

        args.Handled = true;
    }

    private void OnEmagged(EntityUid uid, FaxMachineComponent component, ref GotEmaggedEvent args)
    {
        if (!_emag.CompareFlag(args.Type, EmagType.Interaction))
            return;

        if (_emag.CheckFlag(uid, EmagType.Interaction))
            return;

        args.Handled = true;
    }

    private void OnPacketReceived(EntityUid uid, FaxMachineComponent component, DeviceNetworkPacketEvent args)
    {
        if (!HasComp<DeviceNetworkComponent>(uid) || string.IsNullOrEmpty(args.SenderAddress))
            return;

        if (args.Data.TryGetValue(DeviceNetworkConstants.Command, out string? command))
        {
            switch (command)
            {
                case FaxConstants.FaxPingCommand:
                    var isForSyndie = _emag.CheckFlag(uid, EmagType.Interaction) &&
                                      args.Data.ContainsKey(FaxConstants.FaxSyndicateData);
                    if (!isForSyndie && !component.ResponsePings)
                        return;

                    var payload = new NetworkPayload()
                    {
                        { DeviceNetworkConstants.Command, FaxConstants.FaxPongCommand },
                        { FaxConstants.FaxNameData, component.FaxName }
                    };
                    _deviceNetworkSystem.QueuePacket(uid, args.SenderAddress, payload);

                    break;
                case FaxConstants.FaxPongCommand:
                    if (!args.Data.TryGetValue(FaxConstants.FaxNameData, out string? faxName))
                        return;

                    component.KnownFaxes[args.SenderAddress] = faxName;

                    UpdateUserInterface(uid, component);

                    break;
                case FaxConstants.FaxPrintCommand:
                    if (!args.Data.TryGetValue(FaxConstants.FaxPaperDataToCopy, out Dictionary<Type, IPhotocopiedComponentData>? dataToCopy) ||
                        !args.Data.TryGetValue(FaxConstants.FaxPaperMetaData, out PhotocopyableMetaData? metaDataToCopy))
                        return;

                    var printout = new FaxPrintout(dataToCopy, metaDataToCopy);
                    Receive(uid, printout, args.SenderAddress);

                    break;
            }
        }
    }

    private void OnToggleInterface(EntityUid uid, FaxMachineComponent component, AfterActivatableUIOpenEvent args)
    {
        UpdateUserInterface(uid, component);
    }

    private void OnSendButtonPressed(EntityUid uid, FaxMachineComponent component, FaxSendMessage args)
    {
        if (HasComp<MobStateComponent>(component.PaperSlot.Item))
            _faxecute.Faxecute(uid, component); // when button pressed it will hurt the mob.
        else
            Send(uid, component, args);
    }

    private void OnRefreshButtonPressed(EntityUid uid, FaxMachineComponent component, FaxRefreshMessage args)
    {
        Refresh(uid, component);
    }

    private void OnDestinationSelected(EntityUid uid, FaxMachineComponent component, FaxDestinationMessage args)
    {
        SetDestination(uid, args.Address, component);
    }

    private void UpdateAppearance(EntityUid uid, FaxMachineComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (TryComp<FaxableObjectComponent>(component.PaperSlot.Item, out var faxable))
            component.InsertingState = faxable.InsertingState;


        if (component.InsertingTimeRemaining > 0)
        {
            _appearanceSystem.SetData(uid, FaxMachineVisuals.VisualState, FaxMachineVisualState.Inserting);
            Dirty(uid, component);
        }
        else if (component.PrintingTimeRemaining > 0)
            _appearanceSystem.SetData(uid, FaxMachineVisuals.VisualState, FaxMachineVisualState.Printing);
        else
            _appearanceSystem.SetData(uid, FaxMachineVisuals.VisualState, FaxMachineVisualState.Normal);
    }
    private void UpdateUserInterface(EntityUid uid, FaxMachineComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var isPaperInserted = component.PaperSlot.Item != null;
        var canSend = isPaperInserted &&
                      component.DestinationFaxAddress != null &&
                      component.SendTimeoutRemaining <= 0 &&
                      component.InsertingTimeRemaining <= 0;
        var state = new FaxUiState(component.FaxName, component.KnownFaxes, canSend, isPaperInserted, component.DestinationFaxAddress);
        _userInterface.SetUiState(uid, FaxUiKey.Key, state);
    }

    /// <summary>
    ///     Set fax destination address not checking if he knows it exists
    /// </summary>
    public void SetDestination(EntityUid uid, string destAddress, FaxMachineComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        component.DestinationFaxAddress = destAddress;

        UpdateUserInterface(uid, component);
    }

    /// <summary>
    ///     Clears current known fax info and make network scan ping
    ///     Adds special data to  payload if it was emagged to identify itself as a Syndicate
    /// </summary>
    public void Refresh(EntityUid uid, FaxMachineComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        component.DestinationFaxAddress = null;
        component.KnownFaxes.Clear();

        var payload = new NetworkPayload()
        {
            { DeviceNetworkConstants.Command, FaxConstants.FaxPingCommand }
        };

        if (_emag.CheckFlag(uid, EmagType.Interaction))
            payload.Add(FaxConstants.FaxSyndicateData, true);

        _deviceNetworkSystem.QueuePacket(uid, null, payload);
    }

    /// <summary>
    ///     Sends message to addressee if paper is set and a known fax is selected
    ///     A timeout is set after sending
    /// </summary>
    public void Send(EntityUid uid, FaxMachineComponent? component, FaxSendMessage args)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.PaperSlot.Item is not { } sendEntity)
            return;

        if (component.DestinationFaxAddress == null)
            return;

        if (!component.KnownFaxes.TryGetValue(component.DestinationFaxAddress, out var faxName))
            return;

        if (!_photocopierSystem.TryGetPhotocopyableMetaData(sendEntity, out var metaData))
            return;

        var dataToCopy = _photocopierSystem.GetDataToCopyFromEntity(sendEntity);
        if (dataToCopy.Count == 0)
            return;

        //ss220 autogamma update
        var faxEvent = new FaxSendAttemptEvent(uid, component.DestinationFaxAddress, component.FaxName);
        RaiseLocalEvent(faxEvent);
        if (faxEvent.Cancelled)
        {
            _popupSystem.PopupEntity(Loc.GetString("fax-machine-popup-copy-error"), uid, PopupType.SmallCaution);
            return;
        }
        //ss220 autogamma update

        TryComp<NameModifierComponent>(sendEntity, out var nameMod); //ss220 autogamma update
        TryComp<LabelComponent>(sendEntity, out var labelComponent);

        var payload = new NetworkPayload()
        {
            { DeviceNetworkConstants.Command, FaxConstants.FaxPrintCommand },
            { FaxConstants.FaxPaperDataToCopy, dataToCopy },
            { FaxConstants.FaxPaperMetaData, metaData },
        };

        var contentToLog = GetPaperContent(sendEntity) ?? "";

        _deviceNetworkSystem.QueuePacket(uid, component.DestinationFaxAddress, payload);

        _adminLogger.Add(LogType.Action,
            LogImpact.Low,
            $"{ToPrettyString(args.Actor):actor} " +
            $"sent fax from \"{component.FaxName}\" {ToPrettyString(uid):tool} " +
            $"to \"{faxName}\" ({component.DestinationFaxAddress}) " +
            $"of {ToPrettyString(sendEntity):subject}: {contentToLog}");

        component.SendTimeoutRemaining += component.SendTimeout;
        _audioSystem.PlayPvs(component.SendSound, uid);
        UpdateUserInterface(uid, component);
    }

    /// <summary>
    ///     Accepts a new message and adds it to the queue to print
    ///     If has parameter "notifyAdmins" also output a special message to admin chat.
    /// </summary>
    public void Receive(EntityUid uid, FaxPrintout printout, string? fromAddress = null, FaxMachineComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        var faxName = Loc.GetString("fax-machine-popup-source-unknown");
        if (fromAddress != null && component.KnownFaxes.TryGetValue(fromAddress, out var fax)) // If message received from unknown fax address
            faxName = fax;

        _popupSystem.PopupEntity(Loc.GetString("fax-machine-popup-received", ("from", faxName)), uid);
        _appearanceSystem.SetData(uid, FaxMachineVisuals.VisualState, FaxMachineVisualState.Printing);

        if (component.NotifyAdmins)
            NotifyAdmins(faxName);

        component.PrintingQueue.Enqueue(printout);
    }

    private string? GetPaperContent(EntityUid uid)
    {
        return TryComp<PaperComponent>(uid, out var component) ? component.Content : null;
    }

    private void SpawnPaperFromQueue(EntityUid uid, FaxMachineComponent? component = null)
    {
        if (!Resolve(uid, ref component) || component.PrintingQueue.Count == 0)
            return;

        var printout = component.PrintingQueue.Dequeue();
        var coords = Transform(uid).Coordinates;
        var possiblePrinted = _photocopierSystem.SpawnCopy(coords, printout.MetaData, printout.DataToCopy);
        if (possiblePrinted is not { } printed)
            return;

        var contentToLog = GetPaperContent(printed) ?? "";

        _adminLogger.Add(LogType.Action, LogImpact.Low, $"\"{component.FaxName}\" {ToPrettyString(uid):tool} printed {ToPrettyString(printed):subject}: {contentToLog}");
    }

    private void NotifyAdmins(string faxName)
    {
        _chat.SendAdminAnnouncement(Loc.GetString("fax-machine-chat-notify", ("fax", faxName)));
        _audioSystem.PlayGlobal("/Audio/Machines/high_tech_confirm.ogg", Filter.Empty().AddPlayers(_adminManager.ActiveAdmins), false, AudioParams.Default.WithVolume(-8f));
    }
}

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Steamworks.Data;


namespace Steamworks
{
	internal unsafe partial class ISteamInventory : SteamInterface
	{
		public const string Version = "STEAMINVENTORY_INTERFACE_V003";
		
		internal ISteamInventory( bool IsGameServer )
		{
			SetupInterface( IsGameServer );
		}
		
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_SteamInventory_v003", CallingConvention = Platform.CC)]
		internal static extern IntPtr SteamAPI_SteamInventory_v003();
		public override IntPtr GetUserInterfacePointer() => SteamAPI_SteamInventory_v003();
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_SteamGameServerInventory_v003", CallingConvention = Platform.CC)]
		internal static extern IntPtr SteamAPI_SteamGameServerInventory_v003();
		public override IntPtr GetServerInterfacePointer() => SteamAPI_SteamGameServerInventory_v003();
		
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetResultStatus", CallingConvention = Platform.CC)]
		private static extern Result _GetResultStatus( IntPtr self, SteamInventoryResult_t resultHandle );
		
		#endregion
		internal Result GetResultStatus( SteamInventoryResult_t resultHandle )
		{
			var returnValue = _GetResultStatus( Self, resultHandle );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetResultItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetResultItems( IntPtr self, SteamInventoryResult_t resultHandle, [In,Out] SteamItemDetails_t[]? pOutItemsArray, ref uint punOutItemsArraySize );
		
		#endregion
		internal bool GetResultItems( SteamInventoryResult_t resultHandle, [In,Out] SteamItemDetails_t[]?  pOutItemsArray, ref uint punOutItemsArraySize )
		{
			var returnValue = _GetResultItems( Self, resultHandle, pOutItemsArray, ref punOutItemsArraySize );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetResultItemProperty", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetResultItemProperty( IntPtr self, SteamInventoryResult_t resultHandle, uint unItemIndex, IntPtr pchPropertyName, IntPtr pchValueBuffer, ref uint punValueBufferSizeOut );
		
		#endregion
		internal bool GetResultItemProperty( SteamInventoryResult_t resultHandle, uint unItemIndex, string? pchPropertyName, out string pchValueBuffer )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			using var mem__pchValueBuffer = Helpers.TakeMemory();
			uint szpunValueBufferSizeOut = (1024 * 32);
			var returnValue = _GetResultItemProperty( Self, resultHandle, unItemIndex, str__pchPropertyName.Pointer, mem__pchValueBuffer, ref szpunValueBufferSizeOut );
			pchValueBuffer = Helpers.MemoryToString( mem__pchValueBuffer );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetResultTimestamp", CallingConvention = Platform.CC)]
		private static extern uint _GetResultTimestamp( IntPtr self, SteamInventoryResult_t resultHandle );
		
		#endregion
		internal uint GetResultTimestamp( SteamInventoryResult_t resultHandle )
		{
			var returnValue = _GetResultTimestamp( Self, resultHandle );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_CheckResultSteamID", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _CheckResultSteamID( IntPtr self, SteamInventoryResult_t resultHandle, SteamId steamIDExpected );
		
		#endregion
		internal bool CheckResultSteamID( SteamInventoryResult_t resultHandle, SteamId steamIDExpected )
		{
			var returnValue = _CheckResultSteamID( Self, resultHandle, steamIDExpected );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_DestroyResult", CallingConvention = Platform.CC)]
		private static extern void _DestroyResult( IntPtr self, SteamInventoryResult_t resultHandle );
		
		#endregion
		internal void DestroyResult( SteamInventoryResult_t resultHandle )
		{
			_DestroyResult( Self, resultHandle );
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetAllItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetAllItems( IntPtr self, ref SteamInventoryResult_t pResultHandle );
		
		#endregion
		internal bool GetAllItems( ref SteamInventoryResult_t pResultHandle )
		{
			var returnValue = _GetAllItems( Self, ref pResultHandle );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetItemsByID", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetItemsByID( IntPtr self, ref SteamInventoryResult_t pResultHandle, ref InventoryItemId pInstanceIDs, uint unCountInstanceIDs );
		
		#endregion
		internal bool GetItemsByID( ref SteamInventoryResult_t pResultHandle, ref InventoryItemId pInstanceIDs, uint unCountInstanceIDs )
		{
			var returnValue = _GetItemsByID( Self, ref pResultHandle, ref pInstanceIDs, unCountInstanceIDs );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SerializeResult", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _SerializeResult( IntPtr self, SteamInventoryResult_t resultHandle, IntPtr pOutBuffer, ref uint punOutBufferSize );
		
		#endregion
		internal bool SerializeResult( SteamInventoryResult_t resultHandle, IntPtr pOutBuffer, ref uint punOutBufferSize )
		{
			var returnValue = _SerializeResult( Self, resultHandle, pOutBuffer, ref punOutBufferSize );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_DeserializeResult", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _DeserializeResult( IntPtr self, ref SteamInventoryResult_t pOutResultHandle, IntPtr pBuffer, uint unBufferSize, [MarshalAs( UnmanagedType.U1 )] bool bRESERVED_MUST_BE_FALSE );
		
		#endregion
		internal bool DeserializeResult( ref SteamInventoryResult_t pOutResultHandle, IntPtr pBuffer, uint unBufferSize, [MarshalAs( UnmanagedType.U1 )] bool bRESERVED_MUST_BE_FALSE )
		{
			var returnValue = _DeserializeResult( Self, ref pOutResultHandle, pBuffer, unBufferSize, bRESERVED_MUST_BE_FALSE );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GenerateItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GenerateItems( IntPtr self, ref SteamInventoryResult_t pResultHandle, [In,Out] InventoryDefId[]  pArrayItemDefs, [In,Out] uint[]  punArrayQuantity, uint unArrayLength );
		
		#endregion
		internal bool GenerateItems( ref SteamInventoryResult_t pResultHandle, [In,Out] InventoryDefId[]  pArrayItemDefs, [In,Out] uint[]  punArrayQuantity, uint unArrayLength )
		{
			var returnValue = _GenerateItems( Self, ref pResultHandle, pArrayItemDefs, punArrayQuantity, unArrayLength );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GrantPromoItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GrantPromoItems( IntPtr self, ref SteamInventoryResult_t pResultHandle );
		
		#endregion
		internal bool GrantPromoItems( ref SteamInventoryResult_t pResultHandle )
		{
			var returnValue = _GrantPromoItems( Self, ref pResultHandle );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_AddPromoItem", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _AddPromoItem( IntPtr self, ref SteamInventoryResult_t pResultHandle, InventoryDefId itemDef );
		
		#endregion
		internal bool AddPromoItem( ref SteamInventoryResult_t pResultHandle, InventoryDefId itemDef )
		{
			var returnValue = _AddPromoItem( Self, ref pResultHandle, itemDef );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_AddPromoItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _AddPromoItems( IntPtr self, ref SteamInventoryResult_t pResultHandle, [In,Out] InventoryDefId[]  pArrayItemDefs, uint unArrayLength );
		
		#endregion
		internal bool AddPromoItems( ref SteamInventoryResult_t pResultHandle, [In,Out] InventoryDefId[]  pArrayItemDefs, uint unArrayLength )
		{
			var returnValue = _AddPromoItems( Self, ref pResultHandle, pArrayItemDefs, unArrayLength );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_ConsumeItem", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _ConsumeItem( IntPtr self, ref SteamInventoryResult_t pResultHandle, InventoryItemId itemConsume, uint unQuantity );
		
		#endregion
		internal bool ConsumeItem( ref SteamInventoryResult_t pResultHandle, InventoryItemId itemConsume, uint unQuantity )
		{
			var returnValue = _ConsumeItem( Self, ref pResultHandle, itemConsume, unQuantity );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_ExchangeItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _ExchangeItems( IntPtr self, ref SteamInventoryResult_t pResultHandle, [In,Out] InventoryDefId[]  pArrayGenerate, [In,Out] uint[]  punArrayGenerateQuantity, uint unArrayGenerateLength, [In,Out] InventoryItemId[]  pArrayDestroy, [In,Out] uint[]  punArrayDestroyQuantity, uint unArrayDestroyLength );
		
		#endregion
		internal bool ExchangeItems( ref SteamInventoryResult_t pResultHandle, [In,Out] InventoryDefId[]  pArrayGenerate, [In,Out] uint[]  punArrayGenerateQuantity, uint unArrayGenerateLength, [In,Out] InventoryItemId[]  pArrayDestroy, [In,Out] uint[]  punArrayDestroyQuantity, uint unArrayDestroyLength )
		{
			var returnValue = _ExchangeItems( Self, ref pResultHandle, pArrayGenerate, punArrayGenerateQuantity, unArrayGenerateLength, pArrayDestroy, punArrayDestroyQuantity, unArrayDestroyLength );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_TransferItemQuantity", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _TransferItemQuantity( IntPtr self, ref SteamInventoryResult_t pResultHandle, InventoryItemId itemIdSource, uint unQuantity, InventoryItemId itemIdDest );
		
		#endregion
		internal bool TransferItemQuantity( ref SteamInventoryResult_t pResultHandle, InventoryItemId itemIdSource, uint unQuantity, InventoryItemId itemIdDest )
		{
			var returnValue = _TransferItemQuantity( Self, ref pResultHandle, itemIdSource, unQuantity, itemIdDest );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SendItemDropHeartbeat", CallingConvention = Platform.CC)]
		private static extern void _SendItemDropHeartbeat( IntPtr self );
		
		#endregion
		internal void SendItemDropHeartbeat()
		{
			_SendItemDropHeartbeat( Self );
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_TriggerItemDrop", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _TriggerItemDrop( IntPtr self, ref SteamInventoryResult_t pResultHandle, InventoryDefId dropListDefinition );
		
		#endregion
		internal bool TriggerItemDrop( ref SteamInventoryResult_t pResultHandle, InventoryDefId dropListDefinition )
		{
			var returnValue = _TriggerItemDrop( Self, ref pResultHandle, dropListDefinition );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_TradeItems", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _TradeItems( IntPtr self, ref SteamInventoryResult_t pResultHandle, SteamId steamIDTradePartner, [In,Out] InventoryItemId[]  pArrayGive, [In,Out] uint[]  pArrayGiveQuantity, uint nArrayGiveLength, [In,Out] InventoryItemId[]  pArrayGet, [In,Out] uint[]  pArrayGetQuantity, uint nArrayGetLength );
		
		#endregion
		internal bool TradeItems( ref SteamInventoryResult_t pResultHandle, SteamId steamIDTradePartner, [In,Out] InventoryItemId[]  pArrayGive, [In,Out] uint[]  pArrayGiveQuantity, uint nArrayGiveLength, [In,Out] InventoryItemId[]  pArrayGet, [In,Out] uint[]  pArrayGetQuantity, uint nArrayGetLength )
		{
			var returnValue = _TradeItems( Self, ref pResultHandle, steamIDTradePartner, pArrayGive, pArrayGiveQuantity, nArrayGiveLength, pArrayGet, pArrayGetQuantity, nArrayGetLength );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_LoadItemDefinitions", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _LoadItemDefinitions( IntPtr self );
		
		#endregion
		internal bool LoadItemDefinitions()
		{
			var returnValue = _LoadItemDefinitions( Self );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetItemDefinitionIDs", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetItemDefinitionIDs( IntPtr self, [In,Out] InventoryDefId[]?  pItemDefIDs, ref uint punItemDefIDsArraySize );
		
		#endregion
		internal bool GetItemDefinitionIDs( [In,Out] InventoryDefId[]?  pItemDefIDs, ref uint punItemDefIDsArraySize )
		{
			var returnValue = _GetItemDefinitionIDs( Self, pItemDefIDs, ref punItemDefIDsArraySize );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetItemDefinitionProperty", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetItemDefinitionProperty( IntPtr self, InventoryDefId iDefinition, IntPtr pchPropertyName, IntPtr pchValueBuffer, ref uint punValueBufferSizeOut );
		
		#endregion
		internal bool GetItemDefinitionProperty( InventoryDefId iDefinition, string? pchPropertyName, out string pchValueBuffer )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			using var mem__pchValueBuffer = Helpers.TakeMemory();
			uint szpunValueBufferSizeOut = (1024 * 32);
			var returnValue = _GetItemDefinitionProperty( Self, iDefinition, str__pchPropertyName.Pointer, mem__pchValueBuffer, ref szpunValueBufferSizeOut );
			pchValueBuffer = Helpers.MemoryToString( mem__pchValueBuffer );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_RequestEligiblePromoItemDefinitionsIDs", CallingConvention = Platform.CC)]
		private static extern SteamAPICall_t _RequestEligiblePromoItemDefinitionsIDs( IntPtr self, SteamId steamID );
		
		#endregion
		internal CallResult<SteamInventoryEligiblePromoItemDefIDs_t> RequestEligiblePromoItemDefinitionsIDs( SteamId steamID )
		{
			var returnValue = _RequestEligiblePromoItemDefinitionsIDs( Self, steamID );
			return new CallResult<SteamInventoryEligiblePromoItemDefIDs_t>( returnValue, IsServer );
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetEligiblePromoItemDefinitionIDs", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetEligiblePromoItemDefinitionIDs( IntPtr self, SteamId steamID, [In,Out] InventoryDefId[]  pItemDefIDs, ref uint punItemDefIDsArraySize );
		
		#endregion
		internal bool GetEligiblePromoItemDefinitionIDs( SteamId steamID, [In,Out] InventoryDefId[]  pItemDefIDs, ref uint punItemDefIDsArraySize )
		{
			var returnValue = _GetEligiblePromoItemDefinitionIDs( Self, steamID, pItemDefIDs, ref punItemDefIDsArraySize );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_StartPurchase", CallingConvention = Platform.CC)]
		private static extern SteamAPICall_t _StartPurchase( IntPtr self, [In,Out] InventoryDefId[]  pArrayItemDefs, [In,Out] uint[]  punArrayQuantity, uint unArrayLength );
		
		#endregion
		internal CallResult<SteamInventoryStartPurchaseResult_t> StartPurchase( [In,Out] InventoryDefId[]  pArrayItemDefs, [In,Out] uint[]  punArrayQuantity, uint unArrayLength )
		{
			var returnValue = _StartPurchase( Self, pArrayItemDefs, punArrayQuantity, unArrayLength );
			return new CallResult<SteamInventoryStartPurchaseResult_t>( returnValue, IsServer );
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_RequestPrices", CallingConvention = Platform.CC)]
		private static extern SteamAPICall_t _RequestPrices( IntPtr self );
		
		#endregion
		internal CallResult<SteamInventoryRequestPricesResult_t> RequestPrices()
		{
			var returnValue = _RequestPrices( Self );
			return new CallResult<SteamInventoryRequestPricesResult_t>( returnValue, IsServer );
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetNumItemsWithPrices", CallingConvention = Platform.CC)]
		private static extern uint _GetNumItemsWithPrices( IntPtr self );
		
		#endregion
		internal uint GetNumItemsWithPrices()
		{
			var returnValue = _GetNumItemsWithPrices( Self );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetItemsWithPrices", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetItemsWithPrices( IntPtr self, [In,Out] InventoryDefId[]  pArrayItemDefs, [In,Out] ulong[]  pCurrentPrices, [In,Out] ulong[]  pBasePrices, uint unArrayLength );
		
		#endregion
		internal bool GetItemsWithPrices( [In,Out] InventoryDefId[]  pArrayItemDefs, [In,Out] ulong[]  pCurrentPrices, [In,Out] ulong[]  pBasePrices, uint unArrayLength )
		{
			var returnValue = _GetItemsWithPrices( Self, pArrayItemDefs, pCurrentPrices, pBasePrices, unArrayLength );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_GetItemPrice", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _GetItemPrice( IntPtr self, InventoryDefId iDefinition, ref ulong pCurrentPrice, ref ulong pBasePrice );
		
		#endregion
		internal bool GetItemPrice( InventoryDefId iDefinition, ref ulong pCurrentPrice, ref ulong pBasePrice )
		{
			var returnValue = _GetItemPrice( Self, iDefinition, ref pCurrentPrice, ref pBasePrice );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_StartUpdateProperties", CallingConvention = Platform.CC)]
		private static extern SteamInventoryUpdateHandle_t _StartUpdateProperties( IntPtr self );
		
		#endregion
		internal SteamInventoryUpdateHandle_t StartUpdateProperties()
		{
			var returnValue = _StartUpdateProperties( Self );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_RemoveProperty", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _RemoveProperty( IntPtr self, SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, IntPtr pchPropertyName );
		
		#endregion
		internal bool RemoveProperty( SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, string pchPropertyName )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			var returnValue = _RemoveProperty( Self, handle, nItemID, str__pchPropertyName.Pointer );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SetPropertyString", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _SetProperty( IntPtr self, SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, IntPtr pchPropertyName, IntPtr pchPropertyValue );
		
		#endregion
		internal bool SetProperty( SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, string pchPropertyName, string pchPropertyValue )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			using var str__pchPropertyValue = new Utf8StringToNative( pchPropertyValue );
			var returnValue = _SetProperty( Self, handle, nItemID, str__pchPropertyName.Pointer, str__pchPropertyValue.Pointer );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SetPropertyBool", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _SetProperty( IntPtr self, SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, IntPtr pchPropertyName, [MarshalAs( UnmanagedType.U1 )] bool bValue );
		
		#endregion
		internal bool SetProperty( SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, string pchPropertyName, [MarshalAs( UnmanagedType.U1 )] bool bValue )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			var returnValue = _SetProperty( Self, handle, nItemID, str__pchPropertyName.Pointer, bValue );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SetPropertyInt64", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _SetProperty( IntPtr self, SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, IntPtr pchPropertyName, long nValue );
		
		#endregion
		internal bool SetProperty( SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, string pchPropertyName, long nValue )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			var returnValue = _SetProperty( Self, handle, nItemID, str__pchPropertyName.Pointer, nValue );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SetPropertyFloat", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _SetProperty( IntPtr self, SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, IntPtr pchPropertyName, float flValue );
		
		#endregion
		internal bool SetProperty( SteamInventoryUpdateHandle_t handle, InventoryItemId nItemID, string pchPropertyName, float flValue )
		{
			using var str__pchPropertyName = new Utf8StringToNative( pchPropertyName );
			var returnValue = _SetProperty( Self, handle, nItemID, str__pchPropertyName.Pointer, flValue );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_SubmitUpdateProperties", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _SubmitUpdateProperties( IntPtr self, SteamInventoryUpdateHandle_t handle, ref SteamInventoryResult_t pResultHandle );
		
		#endregion
		internal bool SubmitUpdateProperties( SteamInventoryUpdateHandle_t handle, ref SteamInventoryResult_t pResultHandle )
		{
			var returnValue = _SubmitUpdateProperties( Self, handle, ref pResultHandle );
			return returnValue;
		}
		
		#region FunctionMeta
		[DllImport( Platform.LibraryName, EntryPoint = "SteamAPI_ISteamInventory_InspectItem", CallingConvention = Platform.CC)]
		[return: MarshalAs( UnmanagedType.I1 )]
		private static extern bool _InspectItem( IntPtr self, ref SteamInventoryResult_t pResultHandle, IntPtr pchItemToken );
		
		#endregion
		internal bool InspectItem( ref SteamInventoryResult_t pResultHandle, string pchItemToken )
		{
			using var str__pchItemToken = new Utf8StringToNative( pchItemToken );
			var returnValue = _InspectItem( Self, ref pResultHandle, str__pchItemToken.Pointer );
			return returnValue;
		}
		
	}
}

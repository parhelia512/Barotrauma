﻿using Barotrauma.Networking;
using System;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class CharacterCampaignData
    {
#if DEBUG
        /// <summary>
        /// If enabled, client names must match the name of the character. Useful for testing the campaign with multiple clients running locally:
        /// without this, the clients would all get assigned the same character due to all of them having the same AccountId or Address.
        /// </summary>
        public static bool RequireClientNameMatch = false;
#endif

        public bool HasSpawned;
        
        /// <summary>
        /// Respawning via shuttle has been blocked from permanently dead characters, but it should be possible when the player
        /// chooses a bot from the reserve bench and shuttles are enabled in the campaign.
        /// </summary>
        public bool ChosenNewBotViaShuttle;

        public bool HasItemData
        {
            get { return itemData != null; }
        }

        public CharacterCampaignData(Client client)
        {
            Name = client.Name;
            ClientAddress = client.Connection.Endpoint.Address;
            AccountId = client.AccountId;
            CharacterInfo = client.CharacterInfo;

            healthData = new XElement("health");

            //the character may not be controlled by the client atm, but still exist
            Character character = client.Character ?? CharacterInfo?.Character;

            character?.CharacterHealth?.Save(healthData);
            if (character?.Inventory != null)
            {
                itemData = new XElement("inventory");
                Character.SaveInventory(character.Inventory, itemData);
            }
            OrderData = new XElement("orders");
            if (CharacterInfo != null)
            {
                CharacterInfo.SaveOrderData(CharacterInfo, OrderData);
            }
            if (character?.Wallet.Save() is { } walletSave)
            {
                WalletData = walletSave;
            }
        }

        public void Refresh(Character character, bool refreshHealthData)
        {
            if (refreshHealthData)
            {
                healthData = new XElement("health");
                character.CharacterHealth.Save(healthData);
            }
            if (character.Inventory != null)
            {
                itemData = new XElement("inventory");
                Character.SaveInventory(character.Inventory, itemData);
            }
            OrderData = new XElement("orders");
            CharacterInfo.SaveOrderData(character.Info, OrderData);
            WalletData = character.Wallet.Save();
        }

        public CharacterCampaignData(XElement element)
        {
            Name = element.GetAttributeString("name", "Unnamed");
            string clientEndPointStr = element.GetAttributeString("address", null)
                                       ?? element.GetAttributeString("endpoint", null)
                                       ?? element.GetAttributeString("ip", "");
            ClientAddress = Address.Parse(clientEndPointStr).Fallback(new UnknownAddress());
            string accountIdStr = element.GetAttributeString("accountid", null)
                               ?? element.GetAttributeString("steamid", "");
            AccountId = Networking.AccountId.Parse(accountIdStr);
            ChosenNewBotViaShuttle = element.GetAttributeBool("waitingforshuttle", false);

            foreach (XElement subElement in element.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "character":
                    case "characterinfo":
                        CharacterInfo = new CharacterInfo(new ContentXElement(contentPackage: null, subElement));
                        break;
                    case "inventory":
                        itemData = subElement;
                        break;
                    case "health":
                        healthData = subElement;
                        break;
                    case "orders":
                        OrderData = subElement;
                        break;
                    case Wallet.LowerCaseSaveElementName:
                        WalletData = subElement;
                        break;
                }
            }
        }

        public bool MatchesClient(Client client)
        {
            if (AccountId.TryUnwrap(out var accountId)
                && client.AccountId.TryUnwrap(out var clientId))
            {
                return accountId == clientId;
            }
            else
            {
#if DEBUG
                if (RequireClientNameMatch)
                {
                    return ClientAddress == client.Connection.Endpoint.Address && client.Name == Name;
                }
#endif
                return ClientAddress == client.Connection.Endpoint.Address;
            }
        }

        public bool IsDuplicate(CharacterCampaignData other)
        {
#if DEBUG
            if (RequireClientNameMatch)
            {
                return AccountId == other.AccountId && other.ClientAddress == ClientAddress && Name == other.Name;    
            }
#endif
            return AccountId == other.AccountId && other.ClientAddress == ClientAddress;
        }

        public void Reset()
        {
            itemData = null;
            healthData = null;
            WalletData = null;
        }

        public void ApplyPermadeath()
        {
            Reset();
            CharacterInfo.PermanentlyDead = true;
            GameMain.GameSession?.IncrementPermadeath(AccountId);    
            DebugConsole.NewMessage($"Permadeath applied on {Name}'s CharacterCampaignData.CharacterInfo.");
        }

        public void SpawnInventoryItems(Character character, Inventory inventory)
        {
            if (character == null)
            {
                throw new InvalidOperationException($"Failed to spawn inventory items. Character was null.");
            }
            if (itemData == null)
            {
                throw new InvalidOperationException($"Failed to spawn inventory items for the character \"{character.Name}\". No saved inventory data.");
            }
            character.SpawnInventoryItems(inventory, itemData.FromPackage(null));
        }

        public void ApplyHealthData(Character character, Func<AfflictionPrefab, bool> afflictionPredicate = null)
        {
            CharacterInfo.ApplyHealthData(character, healthData, afflictionPredicate);
        }

        public void ApplyOrderData(Character character)
        {
            CharacterInfo.ApplyOrderData(character, OrderData);
        }

        public void ApplyWalletData(Character character)
        {
            character.Wallet = new Wallet(Option.Some(character), WalletData);
        }

        public XElement Save()
        {
            XElement element = new XElement("CharacterCampaignData",
                new XAttribute("name", Name),
                new XAttribute("address", ClientAddress),
                new XAttribute("accountid", AccountId.TryUnwrap(out var accountId) ? accountId.StringRepresentation : ""),
                new XAttribute("waitingforshuttle", ChosenNewBotViaShuttle));
            CharacterInfo?.Save(element);
            if (itemData != null) { element.Add(itemData); }
            if (healthData != null) { element.Add(healthData); }
            if (OrderData != null) { element.Add(OrderData); }
            if (WalletData != null) { element.Add(WalletData); }

            return element;
        }
    }
}

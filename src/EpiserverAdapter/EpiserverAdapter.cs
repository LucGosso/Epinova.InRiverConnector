﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Epinova.InRiverConnector.EpiserverAdapter.Communication;
using Epinova.InRiverConnector.EpiserverAdapter.Enums;
using Epinova.InRiverConnector.EpiserverAdapter.EpiXml;
using Epinova.InRiverConnector.EpiserverAdapter.Helpers;
using Epinova.InRiverConnector.EpiserverAdapter.Utilities;
using Epinova.InRiverConnector.Interfaces.Enums;
using inRiver.Integration.Configuration;
using inRiver.Integration.Export;
using inRiver.Integration.Interface;
using inRiver.Integration.Logging;
using inRiver.Remoting;
using inRiver.Remoting.Connect;
using inRiver.Remoting.Log;
using inRiver.Remoting.Objects;
using inRiver.Remoting.Query;

namespace Epinova.InRiverConnector.EpiserverAdapter
{
    public class EpiserverAdapter : ServerListener, IOutboundConnector, IChannelListener, ICVLListener
    {
        private bool _started;
        private Configuration _config;

        public new void Start()
        {
            _config = new Configuration(Id);

            ConnectorEventHelper.CleanupOngoingEvents(_config);
            ConnectorEvent connectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.Start, "Connector is starting", 0);

            Entity channel = RemoteManager.DataService.GetEntity(_config.ChannelId, LoadLevel.Shallow);
            if (channel == null)
            {
                _started = false;
                ConnectorEventHelper.UpdateEvent(connectorEvent, "Channel id is not valid, could not find entity with id. Unable to start", -1, true);
                return;
            }

            if (channel.EntityType.Id != "Channel")
            {
                _started = false;
                ConnectorEventHelper.UpdateEvent(connectorEvent, "Channel id is not valid, entity with id is no channel. Unable to start", -1, true);
                return;
            }

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainAssemblyResolve;

            if (!InitConnector())
            {
                return;
            }

            base.Start();
            _started = true;
            ConnectorEventHelper.UpdateEvent(connectorEvent, "Connector has started", 100);
        }

        public new void Stop()
        {
            base.Stop();
            _started = false;
            ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.Stop, "Connector is stopped", 100);
        }

        public new void InitConfigurationSettings()
        {
            ConfigurationManager.Instance.SetConnectorSetting(Id, "PUBLISH_FOLDER", @"C:\temp\Publish\Epi");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "PUBLISH_FOLDER_RESOURCES", @"C:\temp\Publish\Epi\Resources");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "RESOURCE_CONFIGURATION", "Preview");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "LANGUAGE_MAPPING", "<languages><language><epi>en</epi><inriver>en-us</inriver></language></languages>");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "ITEM_TO_SKUs", "false");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "CVL_DATA", "Keys|Values|KeysAndValues");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "BUNDLE_ENTITYTYPES", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, "PACKAGE_ENTITYTYPES", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, "DYNAMIC_PACKAGE_ENTITYTYPES", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, "CHANNEL_ID", "123");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "EPI_CODE_FIELDS", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, "EXCLUDE_FIELDS", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, "EPI_NAME_FIELDS", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, "USE_THREE_LEVELS_IN_COMMERCE", "false");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "HTTP_POST_URL", string.Empty);
            ConfigurationManager.Instance.SetConnectorSetting(Id, ConfigKeys.EpiEndpoint, "https://www.example.com/inriverapi/InriverDataImport/");
            ConfigurationManager.Instance.SetConnectorSetting(Id, ConfigKeys.EpiApiKey, "SomeGreatKey123");
            ConfigurationManager.Instance.SetConnectorSetting(Id, ConfigKeys.EpiTimeout, "1");
            ConfigurationManager.Instance.SetConnectorSetting(Id, "BATCH_SIZE", string.Empty);
        }

        public new bool IsStarted()
        {
            return _started;
        }

        public void Publish(int channelId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            ConnectorEvent publishConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, 
                                                                                      ConnectorEventType.Publish,
                                                                                      $"Publish started for channel: {channelId}",
                                                                                      0);
            var publishStopWatch = Stopwatch.StartNew();
            var resourceIncluded = false;

            try
            {
                var channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Failed to initial publish. Could not find the channel.", -1, true);
                    return;
                }

                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Fetching all channel entities...", 1);
                var channelEntities = ChannelHelper.GetAllEntitiesInChannel(_config.ChannelId, Configuration.ExportEnabledEntityTypes);

                _config.ChannelStructureEntities = channelEntities;
                ChannelHelper.BuildEntityIdAndTypeDict(_config);

                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Done fetching all channel entities", 10);

                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Generating catalog.xml...", 11);
                var epiElements = EpiDocument.GetEPiElements(_config);

                var doc = EpiDocument.CreateImportDocument(channelEntity, 
                                                           EpiElement.GetMetaClassesFromFieldSets(_config), 
                                                           EpiDocument.GetAssociationTypes(_config), 
                                                           epiElements, 
                                                           _config);

                var channelIdentifier = ChannelHelper.GetChannelIdentifier(channelEntity);

                var folderDateTime = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff");

                var zippedfileName = DocumentFileHelper.SaveAndZipDocument(channelIdentifier, doc, folderDateTime, _config);

                IntegrationLogger.Write(LogLevel.Information, $"Catalog saved with the following: " +
                                                              $"Nodes: {epiElements["Nodes"].Count}" +
                                                              $"Entries: {epiElements["Entries"].Count}" +
                                                              $"Relations: {epiElements["Relations"].Count}" +
                                                              $"Associations: {epiElements["Associations"].Count}");

                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Done generating catalog.xml", 25);
                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Generating Resource.xml and saving files to disk...", 26);

                List<StructureEntity> resources = RemoteManager.ChannelService.GetAllChannelStructureEntitiesForTypeFromPath(channelEntity.Id.ToString(), "Resource");

                _config.ChannelStructureEntities.AddRange(resources);

                var resourceDocument = Resources.GetDocumentAndSaveFilesToDisk(resources, _config, folderDateTime);
                DocumentFileHelper.SaveDocument(channelIdentifier, resourceDocument, _config, folderDateTime);

                string resourceZipFile = $"resource_{folderDateTime}.zip";

                DocumentFileHelper.ZipFile(Path.Combine(_config.ResourcesRootPath, folderDateTime, "Resources.xml"), resourceZipFile);
                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Done generating/saving Resource.xml", 50);
                publishStopWatch.Stop();

                if (_config.ActivePublicationMode.Equals(PublicationMode.Automatic))
                {
                    IntegrationLogger.Write(LogLevel.Debug, "Starting automatic import!");
                    ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Sending Catalog.xml to EPiServer...", 51);
                    if (EpiApi.Import(
                            Path.Combine(_config.PublicationsRootPath, folderDateTime, Configuration.ExportFileName),
                            ChannelHelper.GetChannelGuid(channelEntity, _config),
                            _config))
                    {
                        ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Done sending Catalog.xml to EPiServer", 75);
                        EpiApi.SendHttpPost(_config, Path.Combine(_config.PublicationsRootPath, folderDateTime, zippedfileName));
                    }
                    else
                    {
                        ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Error while sending Catalog.xml to EPiServer", -1, true);
                        return;
                    }

                    ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Sending Resources to EPiServer...", 76);

                    if (EpiApi.StartAssetImportIntoEpiServerCommerce(Path.Combine(_config.ResourcesRootPath, folderDateTime, "Resources.xml"), Path.Combine(_config.ResourcesRootPath, folderDateTime), _config))
                    {
                        ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Done sending Resources to EPiServer...", 99);
                        EpiApi.SendHttpPost(_config, Path.Combine(_config.ResourcesRootPath, folderDateTime, resourceZipFile));
                        resourceIncluded = true;
                    }
                    else
                    {
                        ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Error while sending resources to EPiServer", -1, true);
                    }
                }

                if (!publishConnectorEvent.IsError)
                {
                    ConnectorEventHelper.UpdateEvent(publishConnectorEvent, "Publish done!", 100);
                    string channelName =
                        EpiMappingHelper.GetNameForEntity(
                            RemoteManager.DataService.GetEntity(channelId, LoadLevel.Shallow),
                            _config,
                            100);
                    EpiApi.ImportUpdateCompleted(
                        channelName,
                        ImportUpdateCompletedEventType.Publish,
                        resourceIncluded,
                        _config);
                }
            }
            catch (Exception exception)
            {
                IntegrationLogger.Write(LogLevel.Error, "Exception in Publish", exception);
                ConnectorEventHelper.UpdateEvent(publishConnectorEvent, exception.Message, -1, true);
            }
            finally
            {
                _config.EntityIdAndType = new Dictionary<int, string>();
                _config.ChannelStructureEntities = new List<StructureEntity>();
                _config.ChannelEntities = new Dictionary<int, Entity>();
            }
        }

        public void UnPublish(int channelId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            IntegrationLogger.Write(LogLevel.Information, string.Format("Unpublish on channel: {0} called. No action made.", channelId));
        }

        public void Synchronize(int channelId)
        {
        }

        public void ChannelEntityAdded(int channelId, int entityId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            _config.ChannelStructureEntities = new List<StructureEntity>();

            IntegrationLogger.Write(LogLevel.Debug, string.Format("Received entity added for entity {0} in channel {1}", entityId, channelId));
            ConnectorEvent entityAddedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelEntityAdded, string.Format("Received entity added for entity {0} in channel {1}", entityId, channelId), 0);

            bool resourceIncluded = false;
            Stopwatch entityAddedStopWatch = new Stopwatch();

            entityAddedStopWatch.Start();

            try
            {
                Entity channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(
                        entityAddedConnectorEvent,
                        "Failed to initial ChannelLinkAdded. Could not find the channel.",
                        -1,
                        true);
                    return;
                }

                List<StructureEntity> addedStructureEntities =
                    ChannelHelper.GetStructureEntitiesForEntityInChannel(_config.ChannelId, entityId);

                foreach (StructureEntity addedStructureEntity in addedStructureEntities)
                {
                    _config.ChannelStructureEntities.Add(
                        ChannelHelper.GetParentStructureEntity(
                            _config.ChannelId,
                            addedStructureEntity.ParentId,
                            addedStructureEntity.EntityId,
                            addedStructureEntities));
                }

                _config.ChannelStructureEntities.AddRange(addedStructureEntities);

                string targetEntityPath = ChannelHelper.GetTargetEntityPath(entityId, addedStructureEntities);

                foreach (
                    StructureEntity childStructureEntity in
                        ChannelHelper.GetChildrenEntitiesInChannel(entityId, targetEntityPath))
                {
                    _config.ChannelStructureEntities.AddRange(
                        ChannelHelper.GetChildrenEntitiesInChannel(
                            childStructureEntity.EntityId,
                            childStructureEntity.Path));
                }

                _config.ChannelStructureEntities.AddRange(
                    ChannelHelper.GetChildrenEntitiesInChannel(entityId, targetEntityPath));
                ChannelHelper.BuildEntityIdAndTypeDict(_config);

                new AddUtility(_config).Add(channelEntity, entityAddedConnectorEvent, out resourceIncluded);
                entityAddedStopWatch.Stop();
            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, "Exception in ChannelEntityAdded", ex);
                ConnectorEventHelper.UpdateEvent(entityAddedConnectorEvent, ex.Message, -1, true);
                return;
            }
            finally
            {
                _config.EntityIdAndType = new Dictionary<int, string>();
            }

            entityAddedStopWatch.Stop();

            IntegrationLogger.Write(LogLevel.Information, string.Format("Add done for channel {0}, took {1}!", channelId, entityAddedStopWatch.GetElapsedTimeFormated()));
            ConnectorEventHelper.UpdateEvent(entityAddedConnectorEvent, "ChannelEntityAdded complete", 100);

            if (!entityAddedConnectorEvent.IsError)
            {
                string channelName = EpiMappingHelper.GetNameForEntity(RemoteManager.DataService.GetEntity(channelId, LoadLevel.Shallow), _config, 100);
                EpiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.EntityAdded, resourceIncluded, _config);
            }

        }

        public void ChannelEntityUpdated(int channelId, int entityId, string data)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            _config.ChannelEntities = new Dictionary<int, Entity>();
            _config.ChannelStructureEntities = new List<StructureEntity>();

            IntegrationLogger.Write(LogLevel.Debug, string.Format("Received entity update for entity {0} in channel {1}", entityId, channelId));
            ConnectorEvent entityUpdatedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelEntityUpdated, string.Format("Received entity update for entity {0} in channel {1}", entityId, channelId), 0);

            Stopwatch entityUpdatedStopWatch = new Stopwatch();
            entityUpdatedStopWatch.Start();

            try
            {
                if (channelId == entityId)
                {
                    ConnectorEventHelper.UpdateEvent(entityUpdatedConnectorEvent, string.Format("ChannelEntityUpdated, updated Entity is the Channel, no action required"), 100);
                    return;
                }

                Entity channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(entityUpdatedConnectorEvent, string.Format("Failed to initial ChannelEntityUpdated. Could not find the channel with id: {0}", channelId), -1, true);
                    return;
                }

                string channelIdentifier = ChannelHelper.GetChannelIdentifier(channelEntity);
                Entity updatedEntity = RemoteManager.DataService.GetEntity(entityId, LoadLevel.DataAndLinks);

                if (updatedEntity == null)
                {
                    IntegrationLogger.Write(LogLevel.Error, string.Format("ChannelEntityUpdated, could not find entity with id: {0}", entityId));
                    ConnectorEventHelper.UpdateEvent(entityUpdatedConnectorEvent, string.Format("ChannelEntityUpdated, could not find entity with id: {0}", entityId), -1, true);

                    return;
                }

                string folderDateTime = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff");

                bool resourceIncluded = false;
                string channelName = EpiMappingHelper.GetNameForEntity(channelEntity, _config, 100);

                _config.ChannelStructureEntities =
                    ChannelHelper.GetStructureEntitiesForEntityInChannel(_config.ChannelId, entityId);

                ChannelHelper.BuildEntityIdAndTypeDict(_config);

                if (updatedEntity.EntityType.Id.Equals("Resource"))
                {
                    XDocument resDoc = Resources.HandleResourceUpdate(updatedEntity, _config, folderDateTime);

                    DocumentFileHelper.SaveDocument(channelIdentifier, resDoc, _config, folderDateTime);

                    string resourceZipFile = string.Format("resource_{0}.zip", folderDateTime);
                    DocumentFileHelper.ZipFile(
                        Path.Combine(_config.ResourcesRootPath, folderDateTime, "Resources.xml"),
                        resourceZipFile);
                    IntegrationLogger.Write(LogLevel.Debug, "Resources saved!");
                    if (_config.ActivePublicationMode.Equals(PublicationMode.Automatic))
                    {
                        IntegrationLogger.Write(LogLevel.Debug, "Starting automatic resource import!");
                        if (EpiApi.StartAssetImportIntoEpiServerCommerce(Path.Combine(_config.ResourcesRootPath, folderDateTime, "Resources.xml"), Path.Combine(_config.ResourcesRootPath, folderDateTime), _config))
                        {
                            EpiApi.SendHttpPost(_config, Path.Combine(_config.ResourcesRootPath, folderDateTime, resourceZipFile));
                            resourceIncluded = true;
                        }
                    }
                }
                else
                {
                    IntegrationLogger.Write(
                        LogLevel.Debug,
                        string.Format(
                            "Updated entity found. Type: {0}, id: {1}",
                            updatedEntity.EntityType.Id,
                            updatedEntity.Id));

                    #region SKU and ChannelNode
                    if (updatedEntity.EntityType.Id.Equals("Item") && data != null && data.Split(',').Contains("SKUs"))
                    {
                        Field currentField = RemoteManager.DataService.GetField(entityId, "SKUs");

                        List<Field> fieldHistory = RemoteManager.DataService.GetFieldHistory(entityId, "SKUs");

                        Field previousField = fieldHistory.FirstOrDefault(f => f.Revision == currentField.Revision - 1);

                        string oldXml = string.Empty;
                        if (previousField != null && previousField.Data != null)
                        {
                            oldXml = (string)previousField.Data;
                        }

                        string newXml = string.Empty;
                        if (currentField.Data != null)
                        {
                            newXml = (string)currentField.Data;
                        }

                        List<XElement> skusToDelete, skusToAdd;
                        BusinessHelper.CompareAndParseSkuXmls(oldXml, newXml, out skusToAdd, out skusToDelete);

                        foreach (XElement skuToDelete in skusToDelete)
                        {
                            EpiApi.DeleteCatalogEntry(skuToDelete.Attribute("id").Value, _config);
                        }

                        if (skusToAdd.Count > 0)
                        {
                            new AddUtility(_config).Add(
                                channelEntity,
                                entityUpdatedConnectorEvent,
                                out resourceIncluded);
                        }
                    }
                    else if (updatedEntity.EntityType.Id.Equals("ChannelNode"))
                    {
                        new AddUtility(_config).Add(
                            channelEntity,
                            entityUpdatedConnectorEvent,
                            out resourceIncluded);

                        entityUpdatedStopWatch.Stop();
                        IntegrationLogger.Write(
                            LogLevel.Information,
                            string.Format(
                                "Update done for channel {0}, took {1}!",
                                channelId,
                                entityUpdatedStopWatch.GetElapsedTimeFormated()));

                        ConnectorEventHelper.UpdateEvent(
                            entityUpdatedConnectorEvent,
                            "ChannelEntityUpdated complete",
                            100);

                        // Fire the complete event
                        EpiApi.ImportUpdateCompleted(
                            channelName,
                            ImportUpdateCompletedEventType.EntityUpdated,
                            resourceIncluded,
                            _config);
                        return;
                    }
                    #endregion

                    if (updatedEntity.EntityType.IsLinkEntityType)
                    {
                        //ChannelEntities will be used for LinkEntity when we get EPiCode with channel prefix
                        if (!_config.ChannelEntities.ContainsKey(updatedEntity.Id))
                        {
                            _config.ChannelEntities.Add(updatedEntity.Id, updatedEntity);
                        }
                    }

                    XDocument doc = EpiDocument.CreateUpdateDocument(channelEntity, updatedEntity, _config);

                    // If data exist in EPiCodeFields.
                    // Update Associations and relations for XDocument doc.
                    if (_config.EpiCodeMapping.ContainsKey(updatedEntity.EntityType.Id) &&
                        data.Split(',').Contains(_config.EpiCodeMapping[updatedEntity.EntityType.Id]))
                    {
                        ChannelHelper.EpiCodeFieldUpdatedAddAssociationAndRelationsToDocument(
                            doc,
                            updatedEntity,
                            _config,
                            channelId);
                    }

                    if (updatedEntity.EntityType.IsLinkEntityType)
                    {
                        List<Link> links = RemoteManager.DataService.GetLinksForLinkEntity(updatedEntity.Id);
                        if (links.Count > 0)
                        {
                            string parentId = ChannelPrefixHelper.GetEpiserverCode(links.First().Source.Id, _config);

                            EpiApi.UpdateLinkEntityData(updatedEntity, channelId, channelEntity, _config, parentId);
                        }
                    }

                    string zippedName = DocumentFileHelper.SaveAndZipDocument(channelIdentifier, doc, folderDateTime, _config);

                    if (_config.ActivePublicationMode.Equals(PublicationMode.Automatic))
                    {
                        IntegrationLogger.Write(LogLevel.Debug, "Starting automatic import!");
                        if (EpiApi.Import(Path.Combine(_config.PublicationsRootPath, folderDateTime, "Catalog.xml"), ChannelHelper.GetChannelGuid(channelEntity, _config), _config))
                        {
                            EpiApi.SendHttpPost(_config, Path.Combine(_config.PublicationsRootPath, folderDateTime, zippedName));
                        }
                    }
                }

                EpiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.EntityUpdated, resourceIncluded, _config);
                entityUpdatedStopWatch.Stop();

            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, "Exception in ChannelEntityUpdated", ex);
                ConnectorEventHelper.UpdateEvent(entityUpdatedConnectorEvent, ex.Message, -1, true);
            }
            finally
            {
                _config.ChannelStructureEntities = new List<StructureEntity>();
                _config.EntityIdAndType = new Dictionary<int, string>();
                _config.ChannelEntities = new Dictionary<int, Entity>();
            }

            IntegrationLogger.Write(LogLevel.Information, string.Format("Update done for channel {0}, took {1}!", channelId, entityUpdatedStopWatch.GetElapsedTimeFormated()));
            ConnectorEventHelper.UpdateEvent(entityUpdatedConnectorEvent, "ChannelEntityUpdated complete", 100);
        }

        public void ChannelEntityDeleted(int channelId, Entity deletedEntity)
        {
            int entityId = deletedEntity.Id;
            if (channelId != _config.ChannelId)
            {
                return;
            }
            
            Stopwatch deleteStopWatch = new Stopwatch();
            IntegrationLogger.Write(LogLevel.Debug, string.Format("Received entity deleted for entity {0} in channel {1}", entityId, channelId));
            ConnectorEvent entityDeletedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelEntityDeleted, string.Format("Received entity deleted for entity {0} in channel {1}", entityId, channelId), 0);

            try
            {
                IntegrationLogger.Write(LogLevel.Debug, string.Format("Received entity deleted for entity {0} in channel {1}", entityId, channelId));
                deleteStopWatch.Start();

                Entity channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(entityDeletedConnectorEvent, "Failed to initial ChannelEntityDeleted. Could not find the channel.", -1, true);
                    return;
                }

                new DeleteUtility(_config).Delete(channelEntity, -1, deletedEntity, string.Empty);
                deleteStopWatch.Stop();
            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, "Exception in ChannelEntityDeleted", ex);
                ConnectorEventHelper.UpdateEvent(entityDeletedConnectorEvent, ex.Message, -1, true);
                return;
            }
            finally
            {
                _config.EntityIdAndType = new Dictionary<int, string>();
            }

            IntegrationLogger.Write(LogLevel.Information, string.Format("Delete done for channel {0}, took {1}!", channelId, deleteStopWatch.GetElapsedTimeFormated()));
            ConnectorEventHelper.UpdateEvent(entityDeletedConnectorEvent, "ChannelEntityDeleted complete", 100);

            if (!entityDeletedConnectorEvent.IsError)
            {
                string channelName = EpiMappingHelper.GetNameForEntity(RemoteManager.DataService.GetEntity(channelId, LoadLevel.Shallow), _config, 100);
                EpiApi.DeleteCompleted(channelName, DeleteCompletedEventType.EntitiyDeleted, _config);
            }
        }

        public void ChannelEntityFieldSetUpdated(int channelId, int entityId, string fieldSetId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            ChannelEntityUpdated(channelId, entityId, string.Empty);
        }

        public void ChannelEntitySpecificationFieldAdded(int channelId, int entityId, string fieldName)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            ChannelEntityUpdated(channelId, entityId, string.Empty);
        }

        public void ChannelEntitySpecificationFieldUpdated(int channelId, int entityId, string fieldName)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            ChannelEntityUpdated(channelId, entityId, string.Empty);
        }

        public void ChannelLinkAdded(int channelId, int sourceEntityId, int targetEntityId, string linkTypeId, int? linkEntityId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            _config.ChannelStructureEntities = new List<StructureEntity>();

            IntegrationLogger.Write(LogLevel.Debug, string.Format("Received link added for sourceEntityId {0} and targetEntityId {1} in channel {2}", sourceEntityId, targetEntityId, channelId));
            ConnectorEvent linkAddedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelLinkAdded, string.Format("Received link added for sourceEntityId {0} and targetEntityId {1} in channel {2}", sourceEntityId, targetEntityId, channelId), 0);

            bool resourceIncluded;
            Stopwatch linkAddedStopWatch = new Stopwatch();
            try
            {
                linkAddedStopWatch.Start();

                // NEW CODE
                Entity channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(linkAddedConnectorEvent, "Failed to initial ChannelLinkAdded. Could not find the channel.", -1, true);
                    return;
                }

                ConnectorEventHelper.UpdateEvent(linkAddedConnectorEvent, "Fetching channel entities...", 1);

                var existingEntitiesInChannel = ChannelHelper.GetStructureEntitiesForEntityInChannel(_config.ChannelId, targetEntityId);

                //Get Parents EntityStructure from Path
                List<StructureEntity> parents = new List<StructureEntity>();

                foreach (StructureEntity existingEntity in existingEntitiesInChannel)
                {
                    List<string> parentIds = existingEntity.Path.Split('/').ToList();
                    parentIds.Reverse();
                    parentIds.RemoveAt(0);

                    for (int i = 0; i < parentIds.Count - 1; i++)
                    {
                        int entityId = int.Parse(parentIds[i]);
                        int parentId = int.Parse(parentIds[i + 1]);

                        parents.AddRange(RemoteManager.ChannelService.GetAllStructureEntitiesForEntityWithParentInChannel(channelId, entityId, parentId));
                    }
                }

                List<StructureEntity> children = new List<StructureEntity>();

                foreach (StructureEntity existingEntity in existingEntitiesInChannel)
                {
                    string targetEntityPath = ChannelHelper.GetTargetEntityPath(existingEntity.EntityId, existingEntitiesInChannel, existingEntity.ParentId);
                    children.AddRange(RemoteManager.ChannelService.GetAllChannelStructureEntitiesFromPath(targetEntityPath));
                }

                _config.ChannelStructureEntities.AddRange(parents);
                _config.ChannelStructureEntities.AddRange(children);

                // Remove duplicates
                _config.ChannelStructureEntities =
                    _config.ChannelStructureEntities.GroupBy(x => x.EntityId).Select(x => x.First()).ToList();

                //Adding existing Entities. If it occurs more than one time in channel. We can not remove duplicates.
                _config.ChannelStructureEntities.AddRange(existingEntitiesInChannel);

                ChannelHelper.BuildEntityIdAndTypeDict(_config);

                ConnectorEventHelper.UpdateEvent(linkAddedConnectorEvent, "Done fetching channel entities", 10);

                new AddUtility(_config).Add(
                    channelEntity,
                    linkAddedConnectorEvent,
                    out resourceIncluded);

                linkAddedStopWatch.Stop();
            }
            catch (Exception ex)
            {

                IntegrationLogger.Write(LogLevel.Error, "Exception in ChannelLinkAdded", ex);
                ConnectorEventHelper.UpdateEvent(linkAddedConnectorEvent, ex.Message, -1, true);
                return;
            }
            finally
            {
                _config.EntityIdAndType = new Dictionary<int, string>();
            }

            linkAddedStopWatch.Stop();

            IntegrationLogger.Write(LogLevel.Information, string.Format("ChannelLinkAdded done for channel {0}, took {1}!", channelId, linkAddedStopWatch.GetElapsedTimeFormated()));
            ConnectorEventHelper.UpdateEvent(linkAddedConnectorEvent, "ChannelLinkAdded complete", 100);

            if (!linkAddedConnectorEvent.IsError)
            {
                string channelName = EpiMappingHelper.GetNameForEntity(RemoteManager.DataService.GetEntity(channelId, LoadLevel.Shallow), _config, 100);
                EpiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.LinkAdded, resourceIncluded, _config);
            }
        }

        public void ChannelLinkDeleted(int channelId, int sourceEntityId, int targetEntityId, string linkTypeId, int? linkEntityId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            _config.ChannelStructureEntities = new List<StructureEntity>();
            _config.ChannelEntities = new Dictionary<int, Entity>();

            IntegrationLogger.Write(LogLevel.Debug, string.Format("Received link deleted for sourceEntityId {0} and targetEntityId {1} in channel {2}", sourceEntityId, targetEntityId, channelId));
            ConnectorEvent linkDeletedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelLinkDeleted, string.Format("Received link deleted for sourceEntityId {0} and targetEntityId {1} in channel {2}", sourceEntityId, targetEntityId, channelId), 0);

            Stopwatch linkDeletedStopWatch = new Stopwatch();

            try
            {
                linkDeletedStopWatch.Start();

                Entity channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(linkDeletedConnectorEvent, "Failed to initial ChannelLinkDeleted. Could not find the channel.", -1, true);
                    return;
                }

                Entity targetEntity = RemoteManager.DataService.GetEntity(targetEntityId, LoadLevel.DataAndLinks);

                new DeleteUtility(_config).Delete(channelEntity, sourceEntityId, targetEntity, linkTypeId);

                /*
                if (linkEntityId.HasValue)
                {
                    //If its the last one. The linkEntity should be deleted in EPi.
                    Entity linkEntity = RemoteManager.DataService.GetEntity(linkEntityId.Value, LoadLevel.DataAndLinks);

                    if (linkEntity == null)
                    {
                        //Its the last one that were existing on LinkEntity. Send Delete to EPi
                        new DeleteUtility(config).DeleteLinkEntity(channelEntity, linkEntityId.Value);
                    }

                    //new DeleteUtility(config).Delete(channelEntity, -1, linkEntity, string.Empty);
                }*/

                linkDeletedStopWatch.Stop();
            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, "Exception in ChannelLinkDeleted", ex);
                ConnectorEventHelper.UpdateEvent(linkDeletedConnectorEvent, ex.Message, -1, true);
                return;
            }
            finally
            {
                _config.EntityIdAndType = new Dictionary<int, string>();
                _config.ChannelEntities = new Dictionary<int, Entity>();
            }

            linkDeletedStopWatch.Stop();

            IntegrationLogger.Write(LogLevel.Information, string.Format("ChannelLinkDeleted done for channel {0}, took {1}!", channelId, linkDeletedStopWatch.GetElapsedTimeFormated()));
            ConnectorEventHelper.UpdateEvent(linkDeletedConnectorEvent, "ChannelLinkDeleted complete", 100);

            if (!linkDeletedConnectorEvent.IsError)
            {
                string channelName = EpiMappingHelper.GetNameForEntity(RemoteManager.DataService.GetEntity(channelId, LoadLevel.Shallow), _config, 100);
                EpiApi.DeleteCompleted(channelName, DeleteCompletedEventType.LinkDeleted, _config);
            }
        }

                public void ChannelLinkUpdated(int channelId, int sourceEntityId, int targetEntityId, string linkTypeId, int? linkEntityId)
        {
            if (channelId != _config.ChannelId)
            {
                return;
            }

            _config.ChannelStructureEntities = new List<StructureEntity>();

            IntegrationLogger.Write(LogLevel.Debug, string.Format("Received link update for sourceEntityId {0} and targetEntityId {1} in channel {2}", sourceEntityId, targetEntityId, channelId));
            ConnectorEvent linkUpdatedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.ChannelLinkAdded, string.Format("Received link update for sourceEntityId {0} and targetEntityId {1} in channel {2}", sourceEntityId, targetEntityId, channelId), 0);

            bool resourceIncluded;

            Stopwatch linkAddedStopWatch = new Stopwatch();
            try
            {
                linkAddedStopWatch.Start();

                // NEW CODE
                Entity channelEntity = InitiateChannelConfiguration(channelId);
                if (channelEntity == null)
                {
                    ConnectorEventHelper.UpdateEvent(
                        linkUpdatedConnectorEvent,
                        "Failed to initial ChannelLinkUpdated. Could not find the channel.",
                        -1,
                        true);
                    return;
                }

                ConnectorEventHelper.UpdateEvent(linkUpdatedConnectorEvent, "Fetching channel entities...", 1);

                var targetEntityStructure = ChannelHelper.GetEntityInChannelWithParent(_config.ChannelId, targetEntityId, sourceEntityId);

                StructureEntity parentStructureEntity = ChannelHelper.GetParentStructureEntity(_config.ChannelId, sourceEntityId, targetEntityId, targetEntityStructure);

                if (parentStructureEntity != null)
                {
                    _config.ChannelStructureEntities.Add(parentStructureEntity);

                    _config.ChannelStructureEntities.AddRange(
                        ChannelHelper.GetChildrenEntitiesInChannel(
                            parentStructureEntity.EntityId,
                            parentStructureEntity.Path));

                    ChannelHelper.BuildEntityIdAndTypeDict(_config);

                    ConnectorEventHelper.UpdateEvent(
                        linkUpdatedConnectorEvent,
                        "Done fetching channel entities",
                        10);

                    new AddUtility(_config).Add(channelEntity, linkUpdatedConnectorEvent, out resourceIncluded);
                }
                else
                {
                    linkAddedStopWatch.Stop();
                    resourceIncluded = false;
                    IntegrationLogger.Write(LogLevel.Error, string.Format("Not possible to located source entity {0} in channel structure for target entity {1}", sourceEntityId, targetEntityId));
                    ConnectorEventHelper.UpdateEvent(linkUpdatedConnectorEvent, string.Format("Not possible to located source entity {0} in channel structure for target entity {1}", sourceEntityId, targetEntityId), -1, true);
                    return;
                }

                linkAddedStopWatch.Stop();
            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, "Exception in ChannelLinkUpdated", ex);
                ConnectorEventHelper.UpdateEvent(linkUpdatedConnectorEvent, ex.Message, -1, true);
                return;
            }
            finally
            {
                _config.EntityIdAndType = new Dictionary<int, string>();
            }

            linkAddedStopWatch.Stop();

            IntegrationLogger.Write(LogLevel.Information, string.Format("ChannelLinkUpdated done for channel {0}, took {1}!", channelId, linkAddedStopWatch.GetElapsedTimeFormated()));
            ConnectorEventHelper.UpdateEvent(linkUpdatedConnectorEvent, "ChannelLinkUpdated complete", 100);

            if (!linkUpdatedConnectorEvent.IsError)
            {
                string channelName = EpiMappingHelper.GetNameForEntity(RemoteManager.DataService.GetEntity(channelId, LoadLevel.Shallow), _config, 100);
                EpiApi.ImportUpdateCompleted(channelName, ImportUpdateCompletedEventType.LinkUpdated, resourceIncluded, _config);
            }
        }

        public void AssortmentCopiedInChannel(int channelId, int assortmentId, int targetId, string targetType)
        {

        }

        public void CVLValueCreated(string cvlId, string cvlValueKey)
        {
            IntegrationLogger.Write(LogLevel.Information, string.Format("CVL value created event received with key '{0}' from CVL with id {1}", cvlValueKey, cvlId));

            ConnectorEvent cvlValueCreatedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.CVLValueCreated, string.Format("CVL value created event received with key '{0}' from CVL with id {1}", cvlValueKey, cvlId), 0);

            try
            {
                CVLValue val = RemoteManager.ModelService.GetCVLValueByKey(cvlValueKey, cvlId);

                if (val != null)
                {
                    if (!BusinessHelper.CVLValues.Any(cv => cv.CVLId.Equals(cvlId) && cv.Key.Equals(cvlValueKey)))
                    {
                        BusinessHelper.CVLValues.Add(val);
                    }

                    string folderDateTime = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff");
                    new CvlUtility(_config).AddCvl(cvlId, folderDateTime);
                }
                else
                {
                    IntegrationLogger.Write(LogLevel.Error, string.Format("Could not add CVL value with key {0} to CVL with id {1}", cvlValueKey, cvlId));
                    ConnectorEventHelper.UpdateEvent(cvlValueCreatedConnectorEvent, string.Format("Could not add CVL value with key {0} to CVL with id {1}", cvlValueKey, cvlId), -1, true);
                }
            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, string.Format("Could not add CVL value with key {0} to CVL with id {1}", cvlValueKey, cvlId), ex);
                ConnectorEventHelper.UpdateEvent(cvlValueCreatedConnectorEvent, ex.Message, -1, true);
            }

            ConnectorEventHelper.UpdateEvent(cvlValueCreatedConnectorEvent, "CVLValueCreated complete", 100);

        }

        public void CVLValueUpdated(string cvlId, string cvlValueKey)
        {
            ConnectorEvent cvlValueUpdatedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.CVLValueCreated, string.Format("CVL value updated for CVL {0} and key {1}", cvlId, cvlValueKey), 0);
            IntegrationLogger.Write(LogLevel.Debug, string.Format("CVL value updated for CVL {0} and key {1}", cvlId, cvlValueKey));

            try
            {
                RemoteManager.ModelService.ReloadCacheForCVLValuesForCVL(cvlId);
                CVLValue val = RemoteManager.ModelService.GetCVLValueByKey(cvlValueKey, cvlId);
                if (val != null)
                {
                    CVLValue cachedValue = BusinessHelper.CVLValues.FirstOrDefault(cv => cv.CVLId.Equals(cvlId) && cv.Key.Equals(cvlValueKey));
                    if (cachedValue == null)
                    {
                        return;
                    }

                    string folderDateTime = DateTime.Now.ToString("yyyyMMdd-HHmmss.fff");
                    new CvlUtility(_config).AddCvl(cvlId, folderDateTime);

                    if (_config.ActiveCVLDataMode == CVLDataMode.KeysAndValues || _config.ActiveCVLDataMode == CVLDataMode.Values)
                    {
                        List<FieldType> allFieldTypes = RemoteManager.ModelService.GetAllFieldTypes();
                        List<FieldType> allFieldsWithThisCvl = allFieldTypes.FindAll(ft => ft.CVLId == cvlId);
                        Query query = new Query
                        {
                            Join = Join.Or,
                            Criteria = new List<Criteria>()
                        };

                        foreach (FieldType fieldType in allFieldsWithThisCvl)
                        {
                            Criteria criteria = new Criteria
                            {
                                FieldTypeId = fieldType.Id,
                                Operator = Operator.Equal,
                                Value = cvlValueKey
                            };

                            query.Criteria.Add(criteria);
                        }

                        List<Entity> entitesWithThisCvlInPim = RemoteManager.DataService.Search(query, LoadLevel.Shallow);
                        if (entitesWithThisCvlInPim.Count == 0)
                        {
                            IntegrationLogger.Write(LogLevel.Debug, string.Format("CVL value updated complete"));

                            ConnectorEventHelper.UpdateEvent(cvlValueUpdatedConnectorEvent, "CVLValueUpdated complete, no action was needed", 100);
                            return;
                        }

                        List<StructureEntity> channelEntities = ChannelHelper.GetAllEntitiesInChannel(_config.ChannelId, Configuration.ExportEnabledEntityTypes);

                        List<Entity> entitesToUpdate = new List<Entity>();

                        foreach (Entity entity in entitesWithThisCvlInPim)
                        {
                            if (channelEntities.Any() && channelEntities.Exists(i => i.EntityId.Equals(entity.Id)))
                            {
                                entitesToUpdate.Add(entity);
                            }
                        }

                        foreach (Entity entity in entitesToUpdate)
                        {
                            ChannelEntityUpdated(_config.ChannelId, entity.Id, string.Empty);
                        }
                    }
                }
                else
                {
                    IntegrationLogger.Write(LogLevel.Error, string.Format("Could not update CVL value with key {0} for CVL with id {1}", cvlValueKey, cvlId));
                    ConnectorEventHelper.UpdateEvent(cvlValueUpdatedConnectorEvent, string.Format("Could not update CVL value with key {0} for CVL with id {1}", cvlValueKey, cvlId), -1, true);
                }
            }
            catch (Exception ex)
            {
                IntegrationLogger.Write(LogLevel.Error, string.Format("Could not add CVL value {0} to CVL with id {1}", cvlValueKey, cvlId), ex);
                ConnectorEventHelper.UpdateEvent(cvlValueUpdatedConnectorEvent, ex.Message, -1, true);
            }

            IntegrationLogger.Write(LogLevel.Debug, string.Format("CVL value updated complete"));
            ConnectorEventHelper.UpdateEvent(cvlValueUpdatedConnectorEvent, "CVLValueUpdated complete", 100);
        }

        public void CVLValueDeleted(string cvlId, string cvlValueKey)
        {
            IntegrationLogger.Write(LogLevel.Information, string.Format("CVL value deleted event received with key '{0}' from CVL with id {1}", cvlValueKey, cvlId));
            ConnectorEvent cvlValueDeletedConnectorEvent = ConnectorEventHelper.InitiateEvent(_config, ConnectorEventType.CVLValueDeleted, string.Format("CVL value deleted event received with key '{0}' from CVL with id {1}", cvlValueKey, cvlId), 0);

            if (BusinessHelper.CVLValues.RemoveAll(cv => cv.CVLId.Equals(cvlId) && cv.Key.Equals(cvlValueKey)) < 1)
            {
                IntegrationLogger.Write(LogLevel.Error, string.Format("Could not remove CVL value with key {0} from CVL with id {1}", cvlValueKey, cvlId));
                ConnectorEventHelper.UpdateEvent(cvlValueDeletedConnectorEvent, string.Format("Could not remove CVL value with key {0} from CVL with id {1}", cvlValueKey, cvlId), -1, true);

                return;
            }

            ConnectorEventHelper.UpdateEvent(cvlValueDeletedConnectorEvent, "CVLValueDeleted complete", 100);
        }

        public void CVLValueDeletedAll(string cvlId)
        {

        }

        private Entity InitiateChannelConfiguration(int channelId)
        {
            Entity channel = RemoteManager.DataService.GetEntity(channelId, LoadLevel.DataOnly);
            if (channel == null)
            {
                IntegrationLogger.Write(LogLevel.Error, "Could not find channel");
                return null;
            }

            ChannelHelper.UpdateChannelSettings(channel, _config);
            return channel;
        }

        private bool InitConnector()
        {
            bool result = true;
            try
            {
                if (!Directory.Exists(_config.PublicationsRootPath))
                {
                    try
                    {
                        Directory.CreateDirectory(_config.PublicationsRootPath);
                    }
                    catch (Exception exception)
                    {
                        result = false;
                        IntegrationLogger.Write(LogLevel.Error, string.Format("Root directory {0} is missing, and not creatable.\n", _config.PublicationsRootPath), exception);
                    }
                }
            }
            catch (Exception ex)
            {
                result = false;
                IntegrationLogger.Write(LogLevel.Error, "Error in InitConnector", ex);
            }

            return result;
        }

        private Assembly CurrentDomainAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (folderPath != null)
            {
                int ix = folderPath.LastIndexOf("\\", StringComparison.Ordinal);
                if (ix == -1)
                {
                    return null;
                }

                folderPath = folderPath.Substring(0, ix);
                string assemblyPath = Path.Combine(folderPath, new AssemblyName(args.Name).Name + ".dll");

                if (File.Exists(assemblyPath) == false)
                {
                    assemblyPath = Path.Combine(folderPath + "\\OutboundConnectors\\", new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(assemblyPath) == false)
                    {
                        return null;
                    }
                }

                Assembly assembly = Assembly.LoadFrom(assemblyPath);
                return assembly;
            }

            return null;
        }
    }
}
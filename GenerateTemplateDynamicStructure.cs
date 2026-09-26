using Centrix.CRM.Model;
using Centrix.Plugins.BI.Helpers;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace Centrix.Plugins.BI.Quote.QuoteHandler
{
    public static class GenerateTemplateDynamicStructure
    {
        public static void ExecuteOnUpdate(IOrganizationService service, ref TraceLogger log, IPluginExecutionContext pluginContext)
        {
            log.Trace("----------------- GenerateTemplateDynamicStructure -----------------");


            try
            {
                if (!pluginContext.InputParameters.Contains("Target"))
                {
                    log.Trace("Missing Target Parameter");
                    throw new InvalidPluginExecutionException("Missing Target Parameter");
                }

                var Target = pluginContext.InputParameters["Target"] as Entity;

                Centrix.CRM.Model.Quote quote = Target.ToEntity<Centrix.CRM.Model.Quote>();

                if (Target == null)
                {
                    log.Trace("Target Parameter is null");
                    throw new InvalidPluginExecutionException("Target Parameter is null");
                }

                foreach (var attr in quote.Attributes)
                {
                    log.Trace($"Quote Attribute: {attr.Key} ---- Quote Attribute Value : {attr.Value}");
                }
                if (!quote.Contains(Centrix.CRM.Model.Quote.Fields.StateCode))
                {
                    log.Trace("Missing StateCode");
                    return;
                }

                if(quote.StateCode != null && quote.StateCode.Value != Centrix.CRM.Model.Quote_StateCode.Active)
                {
                    log.Trace($"Quote State is not Active : {quote.StateCode.Value}");
                    return;
                }

                if (Target.LogicalName != CRM.Model.Quote.EntityLogicalName)
                {
                    log.Trace("Target entity is not quote");
                    throw new InvalidPluginExecutionException("Target entity is not quote");
                }

                if (!Utility.isCpqMode(service, Target.ToEntityReference(), ref log))
                {
                    log.Trace("CPQ Mode is not active.");
                    return;
                }



                log.Trace($"Target Quote Id: {Target.Id}");

                // Récupérer l'ownerRef depuis le PostImage (évite un Retrieve supplémentaire)
                EntityReference priceLevelRef = null;
                if (pluginContext.PostEntityImages.Contains("PostImage"))
                {
                    var postImage = pluginContext.PostEntityImages["PostImage"];
                    priceLevelRef = postImage?.GetAttributeValue<EntityReference>("pricelevelid");
                }
                log.Trace($"PriceLevelidRef from PostImage : {priceLevelRef?.Id.ToString() ?? "non disponible"}");

                GenerateQuoteMatrixJson(service, ref log, Target, priceLevelRef);

            }
            catch (Exception ex)
            {
                log.Trace($"An unexpected error occurred in the plugin GenerateTemplateDynamicStructure : {ex.Message}");
                throw new InvalidPluginExecutionException($"An unexpected error occurred in the plugin GenerateTemplateDynamicStructure : {ex.Message}");
            }
            finally
            {
                log.Trace("Finish GenerateTemplateDynamicStructure..");
            }
        }

        private static void GenerateQuoteMatrixJson(IOrganizationService service, ref TraceLogger log, Entity quote, EntityReference priceLevelRef)
        {

            log.Trace("-----------------start GenerateQuoteMatrixJson -----------------");
            List<Entity> quoteTemplateList = new List<Entity>();

            #region 🔥 SUPPRESSION des anciennes lignes de template
            var deleteQuery = new QueryExpression("ctx_quotetemplate")
            {
                ColumnSet = new ColumnSet("ctx_quotetemplateid"), // obligatoire pour delete
                Criteria =
    {
        Conditions =
        {
            new ConditionExpression("ctx_quoteid", ConditionOperator.Equal, quote.Id)
        }
    }
            };

            var existingTemplates = service.RetrieveMultiple(deleteQuery).Entities;

            foreach (var record in existingTemplates)
            {
                service.Delete("ctx_quotetemplate", record.Id);
            }
            #endregion

            Func<Entity, int?> ResolveZone = e =>
            {
                if (e.Contains("ctx_arealocalprice")) return 961410000;
                if (e.Contains("ctx_area1price")) return 961410001;
                if (e.Contains("ctx_area2price")) return 961410002;
                if (e.Contains("ctx_area3price")) return 961410003;
                if (e.Contains("ctx_area4price")) return 961410004;
                if (e.Contains("ctx_area5price")) return 961410005;
                return null;
            };


            #region 🔹 0. Pays de la liste de prix + résolution des produits additionnels (FetchXml du paramétrage)
            EntityReference priceLevelCountryRef = null;

            try
            {
                if (priceLevelRef == null)
                {
                    throw new InvalidPluginExecutionException("Le devis n'a pas de liste de prix (pricelevelid) — traitement bundle ignoré");
                }
                var priceLevelEntity = service.Retrieve("pricelevel", priceLevelRef.Id, new ColumnSet("ctx_countryid"));
                priceLevelCountryRef = priceLevelEntity.GetAttributeValue<EntityReference>("ctx_countryid");
            }
            catch (Exception)
            {
                // si l'appel échoue on continue sans country (le fallback géré plus bas)
                priceLevelCountryRef = null;
            }

            string priceLevelCountryName = priceLevelCountryRef?.Name;
            log.Trace($"Pays de la liste de prix : {priceLevelCountryName ?? "non défini"}");

            var additionalProducts = ResolveAdditionalProducts(service, ref log, priceLevelCountryName, quote.Id, priceLevelRef?.Id);
            var additionalProductIds = new HashSet<Guid>(additionalProducts.Select(a => a.ProductId));
            log.Trace($"Produits additionnels résolus via paramétrage : {additionalProducts.Count}");
            #endregion


            #region 🔹 1. Récupérer les lignes de devis avec infos produit + zone Hors Bundle
            var quoteDetailColumns = new List<string> { "productid", "extendedamount", "ctx_areacpqid", "priceperunit", "ctx_unitpriceafterdiscount", "producttypecode" };

            // Ajouter les champs "valeur" déclarés dans le paramétrage des produits additionnels
            foreach (var field in additionalProducts
                .Select(a => a.ValueFieldName)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim().ToLowerInvariant())
                .Distinct())
            {
                if (!quoteDetailColumns.Contains(field)) quoteDetailColumns.Add(field);
            }

            var query = new QueryExpression("quotedetail")
            {
                ColumnSet = new ColumnSet(quoteDetailColumns.ToArray()),
                Criteria =
    {
        Conditions =
        {
            new ConditionExpression("quoteid", ConditionOperator.Equal, quote.Id),
             new ConditionExpression("producttypecode", ConditionOperator.NotIn, 3, 4)
        }
    },
                LinkEntities =
    {
        new LinkEntity
        {
            LinkFromEntityName = "quotedetail",
            LinkFromAttributeName = "productid",
            LinkToEntityName = "product",
            LinkToAttributeName = "productid",
            Columns = new ColumnSet("name", "parentproductid"),
            EntityAlias = "prod"
        }
    }
            };


            var lines = service.RetrieveMultiple(query).Entities;

            // 🔹 2. Transformer en structure simple
            var data = lines.Select(x =>
            {
                // Produit du devis
                var productRef = x.GetAttributeValue<EntityReference>("productid");
                string productName = productRef?.Name;

                // Produit parent
                string parentName = null;
                Entity zoneEntity = new Entity();

                if (x.Contains("prod.parentproductid"))
                {
                    var parentRef = (EntityReference)((AliasedValue)x["prod.parentproductid"]).Value;
                    parentName = parentRef.Name;
                }

                if (x.Contains("ctx_areacpqid"))
                {
                    zoneEntity = service.Retrieve("ctx_areacpq", x.GetAttributeValue<EntityReference>("ctx_areacpqid").Id, new ColumnSet("ctx_areanumber"));
                }

                return new
                {
                    Produit = parentName ?? productName, // ✅ clé métier
                    Zone = zoneEntity.GetAttributeValue<OptionSetValue>("ctx_areanumber")?.Value,
                    Prix = Math.Round(x.GetAttributeValue<Money>("ctx_unitpriceafterdiscount")?.Value ?? 0, 2),
                    ProductTypeCode = x.GetAttributeValue<OptionSetValue>("producttypecode")?.Value,   // 2 = Bundle
                    ProductId = productRef?.Id,
                    Line = x
                };
            }).ToList();

            // 🔹 Isolation des produits additionnels : productid présent dans le résultat des FetchXml
            var additionalProductItems = data
                .Where(x => x.ProductId.HasValue && additionalProductIds.Contains(x.ProductId.Value))
                .ToList();
            log.Trace($"Lignes de devis identifiées comme produits additionnels : {additionalProductItems.Count}");

            // Lignes "normales" = tout sauf les produits additionnels
            var regularData = data
                .Where(x => !(x.ProductId.HasValue && additionalProductIds.Contains(x.ProductId.Value)))
                .ToList();

            var produits = regularData
                .Where(x => x.Produit != null && x.ProductTypeCode != 2) // exclure les bundles (traités séparément)
                .Select(x => x.Produit)
                .Distinct()
                .ToList();

            foreach (var produit in produits)
            {
                var items = regularData.Where(x => x.Produit == produit);

                // ✅ contrôle doublon (Produit + Zone)
                var duplicates = items
                    .GroupBy(x => x.Zone)
                    .Where(g => g.Count() > 1);

                if (duplicates.Any())
                {
                    throw new InvalidPluginExecutionException(
                        $"Doublon détecté pour '{produit}' zone '{duplicates.First().Key}'"
                    );
                }

                var entity = new Entity("ctx_quotetemplate");

                entity["ctx_name"] = produit;

                // ✅ liaison au devis
                entity["ctx_quoteid"] = new EntityReference("quote", quote.Id);

                foreach (var item in items)
                {
                    switch (item.Zone)
                    {
                        case 961410000:
                            entity["ctx_arealocalprice"] = item.Prix.ToString();
                            break;

                        case 961410001:
                            entity["ctx_area1price"] = item.Prix.ToString();
                            break;

                        case 961410002:
                            entity["ctx_area2price"] = item.Prix.ToString();
                            break;

                        case 961410003:
                            entity["ctx_area3price"] = item.Prix.ToString();
                            break;

                        case 961410004:
                            entity["ctx_area4price"] = item.Prix.ToString();
                            break;

                        case 961410005:
                            entity["ctx_area5price"] = item.Prix.ToString();
                            break;
                    }

                }

                // ✅ FILTRE : exclure lignes vides
                bool hasAtLeastOneValue =
                    entity.Contains("ctx_arealocalprice") ||
                    entity.Contains("ctx_area1price") ||
                    entity.Contains("ctx_area2price") ||
                    entity.Contains("ctx_area3price") ||
                    entity.Contains("ctx_area4price") ||
                    entity.Contains("ctx_area5price");

                if (!hasAtLeastOneValue)
                {
                    log.Trace($"Produit '{produit}' ignoré (aucune zone renseignée)");
                    continue;
                }
                quoteTemplateList.Add(entity);
            }
            #endregion

            #region  🔹 Traitement des bundles (producttypecode = 2)
            var bundleLines = regularData.Where(x => x.ProductTypeCode == 2 && x.ProductId.HasValue).ToList();
            log.Trace($"Bundles trouvés : {bundleLines.Count}");

            if (bundleLines.Any())
            {
                // Récupérer la liste de prix du devis (pricelevelid)

                log.Trace($"Price Level : {priceLevelRef.Id}");
                var processedBundleProducts = new HashSet<Guid>();



                foreach (var bundleLine in bundleLines)
                {
                    if (processedBundleProducts.Contains(bundleLine.ProductId.Value))
                    {
                        log.Trace($"Bundle '{bundleLine.Produit}' déjà traité — ignoré");
                        continue;
                    }
                    processedBundleProducts.Add(bundleLine.ProductId.Value);

                    // 1️⃣ Vérifier que le productpricelevel du bundle a ctx_pricelistitemtypecode = 961410003
                    var checkQuery = new QueryExpression("productpricelevel")
                    {
                        ColumnSet = new ColumnSet("productpricelevelid"),
                        TopCount = 1,
                        Criteria =
                            {
                                Conditions =
                                {
                                    new ConditionExpression("pricelevelid", ConditionOperator.Equal, priceLevelRef.Id),
                                    new ConditionExpression("productid",    ConditionOperator.Equal, bundleLine.ProductId.Value),
                                    new ConditionExpression("ctx_pricelistitemtypecode", ConditionOperator.Equal, 961410003)
                                }
                            }
                    };

                    var checkResult = service.RetrieveMultiple(checkQuery).Entities;

                    if (!checkResult.Any())
                    {
                        log.Trace($"Bundle '{bundleLine.Produit}' : pas de productpricelevel de type 961410003 → ignoré");
                        continue;
                    }

                    log.Trace($"Bundle '{bundleLine.Produit}' : condition 961410003 validée → récupération des lignes type 961410000");

                    // 2️⃣ Récupérer tous les productpricelevel de la même pricelevel avec ctx_pricelistitemtypecode = 961410000
                    var pplQuery = new QueryExpression("productpricelevel")
                    {
                        ColumnSet = new ColumnSet("productid", "amount"),
                        Criteria =
                            {
                                Conditions =
                                {
                                    new ConditionExpression("pricelevelid", ConditionOperator.Equal, priceLevelRef.Id),
                                    new ConditionExpression("ctx_pricelistitemtypecode", ConditionOperator.Equal, 961410000)
                                }
                            }
                    };

                    var pplRecords = service.RetrieveMultiple(pplQuery).Entities;
                    log.Trace($"Productpricelevel (type 961410000) récupérés : {pplRecords.Count}");

                    // 3️⃣ Créer un ctx_quotetemplate par productpricelevel, avec la zone du bundle.
                    //     La zone est toujours résolue via la relation produit->ctx_productareacpq->ctx_areacpq (avec ctx_owningcountry == pricelevel.ctx_countryid)
                    foreach (var ppl in pplRecords)
                    {
                        var pplProductRef = ppl.GetAttributeValue<EntityReference>("productid");
                        var pplAmount = Math.Round(ppl.GetAttributeValue<Money>("amount")?.Value ?? 0, 2);

                        // Produit additionnel → jamais de ligne ctx_quotetemplate
                        if (pplProductRef != null && additionalProductIds.Contains(pplProductRef.Id))
                        {
                            log.Trace($"Produit '{pplProductRef.Name}' (bundle) ignoré : produit additionnel");
                            continue;
                        }

                        // Toujours calculer la zone via ctx_productareacpq -> ctx_areacpq
                        int? zoneVal = null;

                        if (priceLevelCountryRef != null)
                        {
                            try
                            {
                                // Requête : ctx_productareacpq join ctx_areacpq filtrée sur ctx_owningcountry = pricelevel.ctx_countryid
                                var prodAreaQuery = new QueryExpression(CTx_AreAcPQ.EntityLogicalName);
                                prodAreaQuery.TopCount = 1;
                                prodAreaQuery.ColumnSet.AddColumns(CTx_AreAcPQ.Fields.CTx_AreaNumber, CTx_AreAcPQ.Fields.CTx_OwningCountry);
                                prodAreaQuery.Criteria.AddCondition(CTx_AreAcPQ.Fields.CTx_OwningCountry, ConditionOperator.Equal, priceLevelCountryRef.Id);
                                var query_ctx_productareacpq = prodAreaQuery.AddLink(
                                    CTx_ProductAreAcPQ.EntityLogicalName,
                                    CTx_AreAcPQ.PrimaryIdAttribute,
                                    CTx_ProductAreAcPQ.Fields.CTx_AreAcPQId);

                                var query_ctx_productareacpq_product = query_ctx_productareacpq.AddLink(
                                    Product.EntityLogicalName,
                                    CTx_ProductAreAcPQ.Fields.CTx_ProductId,
                                    Product.PrimaryIdAttribute);

                                query_ctx_productareacpq_product.LinkCriteria.AddCondition(Product.PrimaryIdAttribute, ConditionOperator.Equal, pplProductRef.Id);

                                var prodAreaRecords = service.RetrieveMultiple(prodAreaQuery).Entities;
                                if (prodAreaRecords.Any() && prodAreaRecords[0].Contains("ctx_areanumber"))
                                {
                                    var areaOption = (OptionSetValue)prodAreaRecords[0]["ctx_areanumber"];
                                    zoneVal = areaOption?.Value;
                                    log.Trace($"Zone calculée pour bundle '{bundleLine.Produit}' via ctx_productareacpq -> ctx_areacpq = {zoneVal}");
                                }
                                else
                                {
                                    log.Trace($"Aucun ctx_areacpq trouvé pour le produit '{bundleLine.Produit}' et le pays pricelist '{priceLevelCountryRef.Name ?? priceLevelCountryRef.Id.ToString()}'");
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Trace($"Erreur lors de la résolution de la zone pour '{bundleLine.Produit}' : {ex.Message}");
                            }
                        }
                        else
                        {
                            log.Trace($"Impossible de résoudre la zone pour '{bundleLine.Produit}' : ctx_country du pricelevel non défini");
                        }

                        var templateEntity = new Entity("ctx_quotetemplate");
                        templateEntity["ctx_name"] = pplProductRef?.Name;
                        templateEntity["ctx_quoteid"] = new EntityReference("quote", quote.Id);

                        // Zone du bundle → champ de prix correspondant
                        if (!zoneVal.HasValue)
                        {
                            log.Trace($"Zone inconnue pour '{pplProductRef?.Name}' — ignoré");
                            continue;
                        }

                        switch (zoneVal.Value)
                        {
                            case 961410000: templateEntity["ctx_arealocalprice"] = pplAmount.ToString(); break;
                            case 961410001: templateEntity["ctx_area1price"] = pplAmount.ToString(); break;
                            case 961410002: templateEntity["ctx_area2price"] = pplAmount.ToString(); break;
                            case 961410003: templateEntity["ctx_area3price"] = pplAmount.ToString(); break;
                            case 961410004: templateEntity["ctx_area4price"] = pplAmount.ToString(); break;
                            case 961410005: templateEntity["ctx_area5price"] = pplAmount.ToString(); break;
                            default:
                                log.Trace($"Zone inconnue '{zoneVal.Value}' pour '{pplProductRef?.Name}' — ignoré");
                                continue;
                        }
                        if (quoteTemplateList.Any(x => x["ctx_name"].ToString() == pplProductRef?.Name && ResolveZone(x) == zoneVal.Value))
                        {
                            log.Trace($"Produit existant détecté pour Product Bundle '{pplProductRef?.Name}' zone '{zoneVal.Value}' — ignoré");
                            continue;
                        }
                        quoteTemplateList.Add(templateEntity);
                        log.Trace($"ctx_quotetemplate créé (bundle) : '{pplProductRef?.Name}' — amount={pplAmount} — zone={zoneVal.Value}");
                    }
                }
            }
            #endregion


            #region ✅ contrôle doublon (Produit + Zone)

            var duplicatesBundles = quoteTemplateList
                .GroupBy(x => new { Produit = x["ctx_name"], Zone = ResolveZone(x) })
                .Where(g => g.Count() > 1);

            if (duplicatesBundles.Any())
            {
                var first = duplicatesBundles.First();
                throw new InvalidPluginExecutionException(
                    $"Doublon détecté pour '{first.Key.Produit}' zone '{first.Key.Zone}'"
                );
            }
            #endregion


            foreach (var template in quoteTemplateList)
            {
                service.Create(template);
                log.Trace($"ctx_quotetemplate créé : '{template["ctx_name"]}'");
            }

            #region 🔹 Traitement post-création : produits additionnels (FetchXml via AdditionalProductsConfig)
            if (additionalProductItems.Any())
            {
                log.Trace($"Traitement des produits additionnels ({additionalProductItems.Count} item(s))...");

                // Récupérer le 1er ctx_quotetemplate lié au quote (par createdon)
                var firstTemplateQuery = new QueryExpression("ctx_quotetemplate")
                {
                    ColumnSet = new ColumnSet("ctx_quotetemplateid"),
                    Criteria =
                    {
                        Conditions =
                        {
                            new ConditionExpression("ctx_quoteid", ConditionOperator.Equal, quote.Id)
                        }
                    },
                    TopCount = 1,
                    Orders =
                    {
                        new OrderExpression("createdon", OrderType.Ascending)
                    }
                };

                var firstTemplates = service.RetrieveMultiple(firstTemplateQuery).Entities;

                if (firstTemplates.Any())
                {
                    var firstTemplate = firstTemplates[0];
                    var updateEntity = new Entity("ctx_quotetemplate", firstTemplate.Id);

                    foreach (var item in additionalProductItems)
                    {
                        var match = additionalProducts.First(a => a.ProductId == item.ProductId.Value);
                        int slot = match.AdditionalProductNum;

                        if (slot < AdditionalProductsConfig.MinSlot || slot > AdditionalProductsConfig.MaxSlot)
                        {
                            log.Trace($"Produit '{item.Produit}' : slot {slot} hors plage [{AdditionalProductsConfig.MinSlot}-{AdditionalProductsConfig.MaxSlot}] — ignoré");
                            continue;
                        }

                        string nameField = AdditionalProductsConfig.GetNameField(slot);
                        string valueField = AdditionalProductsConfig.GetValueField(slot);
                        string value = GetFieldValueAsText(item.Line, match.ValueFieldName);

                        if (updateEntity.Contains(nameField))
                        {
                            log.Trace($"⚠️ Slot {slot} déjà rempli par '{updateEntity[nameField]}' — écrasé par '{item.Produit}'");
                        }

                        updateEntity[nameField] = item.Produit;
                        updateEntity[valueField] = value;
                        log.Trace($"Slot {slot} → '{item.Produit}' = '{value}' (champ source : {match.ValueFieldName})");
                    }

                    service.Update(updateEntity);
                    log.Trace($"Produits additionnels mis à jour sur ctx_quotetemplate {firstTemplate.Id}");
                }
                else
                {
                    log.Trace("Aucun ctx_quotetemplate trouvé pour le quote — produits additionnels ignorés");
                }
            }
            #endregion

            log.Trace("-----------------Finish GenerateQuoteMatrixJson -----------------");
        }

        /// <summary>
        /// Exécute les FetchXml du paramétrage applicables au pays de la liste de prix
        /// et retourne la liste (GUID produit, N° de slot, champ valeur).
        /// </summary>
        private static List<AdditionalProductMatch> ResolveAdditionalProducts(IOrganizationService service, ref TraceLogger log, string countryName, Guid quoteId, Guid? priceLevelId)
        {
            var result = new List<AdditionalProductMatch>();
            var rules = AdditionalProductsConfig.GetRulesForCountry(countryName);
            log.Trace($"Règles produits additionnels applicables (pays '{countryName ?? "non défini"}') : {rules.Count}");

            foreach (var rule in rules)
            {
                if (string.IsNullOrWhiteSpace(rule.FetchXml))
                {
                    log.Trace($"Règle slot {rule.AdditionalProductNum} : FetchXml vide — ignorée");
                    continue;
                }

                var fetchXml = rule.FetchXml
                    .Replace("{quoteid}", quoteId.ToString())
                    .Replace("{pricelevelid}", priceLevelId?.ToString() ?? Guid.Empty.ToString());

                EntityCollection records;
                try
                {
                    records = service.RetrieveMultiple(new FetchExpression(fetchXml));
                }
                catch (Exception ex)
                {
                    throw new InvalidPluginExecutionException(
                        $"FetchXml invalide pour le produit additionnel {rule.AdditionalProductNum} (pays '{rule.Country}') : {ex.Message}");
                }

                log.Trace($"Règle slot {rule.AdditionalProductNum} : {records.Entities.Count} résultat(s)");

                foreach (var record in records.Entities)
                {
                    var productId = GetProductId(record);
                    if (!productId.HasValue)
                    {
                        log.Trace($"Règle slot {rule.AdditionalProductNum} : résultat '{record.LogicalName}' sans productid — ignoré");
                        continue;
                    }

                    if (result.Any(r => r.ProductId == productId.Value))
                    {
                        log.Trace($"Produit {productId.Value} déjà associé à un slot — règle slot {rule.AdditionalProductNum} ignorée pour ce produit");
                        continue;
                    }

                    result.Add(new AdditionalProductMatch
                    {
                        ProductId = productId.Value,
                        AdditionalProductNum = rule.AdditionalProductNum,
                        ValueFieldName = rule.ValueFieldName
                    });
                    log.Trace($"Produit additionnel : {productId.Value} → slot {rule.AdditionalProductNum}");
                }
            }

            return result;
        }

        /// <summary>GUID du produit : Id si l'entité est un product, sinon l'attribut productid.</summary>
        private static Guid? GetProductId(Entity record)
        {
            if (record.LogicalName == "product") return record.Id;

            if (record.Contains("productid"))
            {
                var raw = record["productid"];
                if (raw is AliasedValue aliased) raw = aliased.Value;
                if (raw is EntityReference er) return er.Id;
                if (raw is Guid g) return g;
            }
            return null;
        }

        /// <summary>Convertit la valeur d'un champ du quotedetail en texte (pour ctx_additionalproductNvalue).</summary>
        private static string GetFieldValueAsText(Entity line, string fieldName)
        {
            if (line == null || string.IsNullOrWhiteSpace(fieldName)) return null;
            fieldName = fieldName.Trim().ToLowerInvariant();
            if (!line.Contains(fieldName) || line[fieldName] == null) return null;

            var raw = line[fieldName];
            if (raw is AliasedValue aliased) raw = aliased.Value;

            switch (raw)
            {
                case Money m:           return Math.Round(m.Value, 2).ToString();
                case decimal d:         return Math.Round(d, 2).ToString();
                case double db:         return Math.Round(db, 2).ToString();
                case OptionSetValue o:  return line.FormattedValues.ContainsKey(fieldName) ? line.FormattedValues[fieldName] : o.Value.ToString();
                case EntityReference r: return r.Name ?? r.Id.ToString();
                case bool b:            return line.FormattedValues.ContainsKey(fieldName) ? line.FormattedValues[fieldName] : b.ToString();
                default:                return raw.ToString();
            }
        }

    }
}

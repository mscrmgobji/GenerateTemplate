using Centrix.CRM.Model;
using Centrix.Plugins.BI.Quote.QuoteHandler;
using CentrixBI_OneShot.DataMigration;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Services.Description;

namespace CentrixBI_OneShot.QuoteTemplate
{
    public static class GenerateTemplateDynamicsStrucutre
    { 
        public static Guid quoteid = new Guid("3cd02ed3-13bc-f111-aaad-6045bd993db4");
        public static void Execute(CrmServiceClient service, ref TraceLogger log)
        {
            log.Trace("----------------- GenerateTemplateDynamicStructure -----------------");
            try
            {
                Quote quote = service.Retrieve("quote", quoteid, new ColumnSet(true)).ToEntity<Quote>();
                EntityReference PriceLevelId = quote.PriceLevelId;
                Console.WriteLine("Execute Old/New Function ? 1/2");
                GenerateQuoteMatrixJson(service, ref log, quote, PriceLevelId);
                //  
            }
            catch (Exception ex)
            {
                // Toute exception (y compris hors GenerateQuoteMatrixJson) → quote template d'erreur, sans bloquer l'utilisateur
                log.Trace($"An unexpected error occurred in the plugin GenerateTemplateDynamicStructure : {ex.Message}");
                HandleException(service, ref log, quoteid, ex);
            }
            finally
            {
                log.Trace("Finish GenerateTemplateDynamicStructure..");
            }
        }

        /// <summary>
        /// Point d'entrée : TOUTE exception (fonctionnelle ou technique) ne bloque pas l'utilisateur :
        /// un seul ctx_quotetemplate "erreur" est créé (ctx_name + ctx_errormessage) puis on sort du plugin.
        /// </summary>
        private static void GenerateQuoteMatrixJson(IOrganizationService service, ref TraceLogger log, Entity quote, EntityReference priceLevelRef)
        {
            try
            {
                GenerateQuoteMatrixJsonCore(service, ref log, quote, priceLevelRef);
            }
            catch (Exception ex)
            {
                HandleException(service, ref log, quote.Id, ex);
                // Sortie normale du plugin : pas d'exception → l'utilisateur CRM n'est pas bloqué
            }
        }

        /// <summary>
        /// Traduit une exception en quote template d'erreur :
        ///   • erreur fonctionnelle → ctx_name = nom de l'erreur métier
        ///   • autre exception      → ctx_name = type de l'exception (ex : NullReferenceException)
        /// </summary>
        private static void HandleException(IOrganizationService service, ref TraceLogger log, Guid quoteId, Exception ex)
        {
            string errorName;
            string errorMessage;

            if (ex is QuoteTemplateFunctionalException fex)
            {
                errorName = fex.ErrorName;
                errorMessage = fex.Message;
            }
            else
            {
                errorName = ex.GetType().Name;
                errorMessage = ex.Message;
                if (ex.InnerException != null)
                    errorMessage += $" | Inner exception: {ex.InnerException.Message}";
            }

            log.Trace($"⚠️ Exception [{errorName}] : {errorMessage}");
            CreateErrorTemplate(service, ref log, quoteId, errorName, errorMessage);
        }

        private static void GenerateQuoteMatrixJsonCore(IOrganizationService service, ref TraceLogger log, Entity quote, EntityReference priceLevelRef)
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


            #region 🔹 0. Pays de la liste de prix + résolution des produits additionnels (nom produit / FetchXml du paramétrage)
            EntityReference priceLevelCountryRef = null;

            try
            {
                if (priceLevelRef == null)
                {
                    throw new InvalidPluginExecutionException("The quote has no price list (pricelevelid) — bundle processing skipped");
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

            // Règles applicables au pays (dans l'ordre du paramétrage). Seules les règles avec FetchXml font un appel serveur.
            var additionalRules = ResolveAdditionalProductRules(service, ref log, priceLevelCountryName, quote.Id, priceLevelRef?.Id);

            // Première règle qui correspond (GUID issu d'un FetchXml OU nom du produit), null sinon
            Func<Guid?, string, AdditionalProductRule> FindAdditionalRule = (productId, productName) =>
                additionalRules.FirstOrDefault(r => r.Matches(productId, productName))?.Rule;
            #endregion


            #region 🔹 1. Récupérer les lignes de devis avec infos produit + zone Hors Bundle
            var quoteDetailColumns = new List<string> { "productid", "productname", "extendedamount", "ctx_areacpqid", "priceperunit", "ctx_unitpriceafterdiscount", "producttypecode" };

            // Ajouter les champs "valeur" déclarés dans le paramétrage des produits additionnels
            foreach (var field in additionalRules
                .Select(a => a.Rule.ValueFieldName)
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
            Columns = new ColumnSet("name", "parentproductid", QuoteTemplateFields.ProductPlatformList),
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

                // Nom utilisé pour les règles "ProductNames" : quotedetail.productname (sinon nom du produit lié)
                string lineProductName = x.GetAttributeValue<string>("productname");
                if (string.IsNullOrWhiteSpace(lineProductName)) lineProductName = productName;

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
                    Line = x,
                    LineProductName = lineProductName, // quotedetail.productname (sinon nom du produit lié)
                    Family = GetMultiSelectLabel(x, "prod." + QuoteTemplateFields.ProductPlatformList), // libellé ctx_platformlist du produit
                    AdditionalRule = FindAdditionalRule(productRef?.Id, lineProductName)
                };
            }).ToList();

            // 🔹 Isolation des produits additionnels : nom du produit OU productid présent dans le résultat d'un FetchXml
            var additionalProductItems = data
                .Where(x => x.AdditionalRule != null)
                .ToList();
            log.Trace($"Lignes de devis identifiées comme produits additionnels : {additionalProductItems.Count}");

            // Lignes "normales" = tout sauf les produits additionnels
            var regularData = data
                .Where(x => x.AdditionalRule == null)
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
                    throw new QuoteTemplateFunctionalException(
                        QuoteTemplateErrors.DoublonProduitZone,
                        $"Duplicate detected for product '{produit}' in zone '{duplicates.First().Key}': several quote lines exist for the same product and the same zone."
                    );
                }

                var entity = new Entity("ctx_quotetemplate");

                entity["ctx_name"] = produit;

                // Famille de produit = libellé(s) ctx_platformlist des produits de la ligne
                foreach (var item in items) AddProductFamily(entity, item.Family);

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
                if (priceLevelRef == null)
                {
                    throw new QuoteTemplateFunctionalException(
                        QuoteTemplateErrors.ListePrixManquante,
                        $"The quote contains {bundleLines.Count} bundle(s) but has no price list (pricelevelid): bundle products cannot be retrieved."
                    );
                }

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
                    //     + le produit parent de chaque produit (jointure product → parentproductid)
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
                            },
                        LinkEntities =
                            {
                                new LinkEntity
                                {
                                    LinkFromEntityName    = "productpricelevel",
                                    LinkFromAttributeName = "productid",
                                    LinkToEntityName      = "product",
                                    LinkToAttributeName   = "productid",
                                    JoinOperator          = JoinOperator.Inner,
                                    Columns               = new ColumnSet("parentproductid", QuoteTemplateFields.ProductPlatformList),
                                    EntityAlias           = "pplprod",
                                    // Uniquement les produits dont ctx_platformlist contient une des valeurs paramétrées
                                    LinkCriteria =
                                    {
                                        Conditions =
                                        {
                                            new ConditionExpression(QuoteTemplateFields.ProductPlatformList, ConditionOperator.ContainValues,
                                                QuoteTemplateFields.BundlePlatformListValues.Cast<object>().ToArray())
                                        }
                                    }
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

                        // Nom du produit = nom du produit PARENT (fallback : nom du produit si pas de parent)
                        EntityReference pplParentRef = null;
                        if (ppl.Contains("pplprod.parentproductid"))
                        {
                            pplParentRef = ((AliasedValue)ppl["pplprod.parentproductid"]).Value as EntityReference;
                        }
                        string bundleProductName = !string.IsNullOrWhiteSpace(pplParentRef?.Name) ? pplParentRef.Name : pplProductRef?.Name;
                        if (pplParentRef == null)
                        {
                            log.Trace($"Produit '{pplProductRef?.Name}' (bundle) : pas de produit parent — nom du produit utilisé");
                        }

                        // Produit additionnel → jamais de ligne ctx_quotetemplate
                        if (pplProductRef != null && FindAdditionalRule(pplProductRef.Id, pplProductRef.Name) != null)
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

                        // Zone du produit → champ de prix correspondant
                        if (!zoneVal.HasValue)
                        {
                            log.Trace($"Zone inconnue pour '{bundleProductName}' (produit '{pplProductRef?.Name}') — ignoré");
                            continue;
                        }

                        string zoneField;
                        switch (zoneVal.Value)
                        {
                            case 961410000: zoneField = "ctx_arealocalprice"; break;
                            case 961410001: zoneField = "ctx_area1price"; break;
                            case 961410002: zoneField = "ctx_area2price"; break;
                            case 961410003: zoneField = "ctx_area3price"; break;
                            case 961410004: zoneField = "ctx_area4price"; break;
                            case 961410005: zoneField = "ctx_area5price"; break;
                            default:
                                log.Trace($"Zone inconnue '{zoneVal.Value}' pour '{bundleProductName}' (produit '{pplProductRef?.Name}') — ignoré");
                                continue;
                        }

                        // ✅ Un seul ctx_quotetemplate par famille (produit parent) :
                        //    on réutilise l'enregistrement existant de même nom et on y ajoute le prix de la zone
                        var templateEntity = quoteTemplateList.FirstOrDefault(x => x.GetAttributeValue<string>("ctx_name") == bundleProductName);
                        bool isNewTemplate = templateEntity == null;

                        if (isNewTemplate)
                        {
                            templateEntity = new Entity("ctx_quotetemplate");
                            templateEntity["ctx_name"] = bundleProductName; // nom du produit parent
                            templateEntity["ctx_quoteid"] = new EntityReference("quote", quote.Id);
                        }
                        else if (templateEntity.Contains(zoneField))
                        {
                            // Zone déjà renseignée pour cette famille → on garde le premier prix
                            log.Trace($"Famille '{bundleProductName}' : zone '{zoneVal.Value}' déjà renseignée ({templateEntity[zoneField]}) — produit '{pplProductRef?.Name}' ({pplAmount}) ignoré");
                            continue;
                        }

                        templateEntity[zoneField] = pplAmount.ToString();

                        // Famille de produit = libellé(s) ctx_platformlist du produit
                        AddProductFamily(templateEntity, GetMultiSelectLabel(ppl, "pplprod." + QuoteTemplateFields.ProductPlatformList));

                        if (isNewTemplate)
                        {
                            quoteTemplateList.Add(templateEntity);
                            log.Trace($"ctx_quotetemplate créé (bundle) : '{bundleProductName}' — produit '{pplProductRef?.Name}' — {zoneField}={pplAmount}");
                        }
                        else
                        {
                            log.Trace($"ctx_quotetemplate complété (bundle) : '{bundleProductName}' — produit '{pplProductRef?.Name}' — {zoneField}={pplAmount}");
                        }
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
                throw new QuoteTemplateFunctionalException(
                    QuoteTemplateErrors.DoublonProduitZone,
                    $"Duplicate detected for product '{first.Key.Produit}' in zone '{first.Key.Zone}': several quote templates exist for the same product and the same zone."
                );
            }
            #endregion


            foreach (var template in quoteTemplateList)
            {
                service.Create(template);
                log.Trace($"ctx_quotetemplate créé : '{template["ctx_name"]}'");
            }

            #region 🔹 Traitement post-création : produits additionnels (via AdditionalProductsConfig)
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

                    // Cache type des champs (Currency ?) + symbole de devise du devis (chargé une seule fois, si besoin)
                    var moneyFieldCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    string currencySymbol = null;
                    bool currencySymbolLoaded = false;

                    foreach (var item in additionalProductItems)
                    {
                        var match = item.AdditionalRule;
                        int slot = match.AdditionalProductNum;

                        if (slot < AdditionalProductsConfig.MinSlot || slot > AdditionalProductsConfig.MaxSlot)
                        {
                            log.Trace($"Produit '{item.Produit}' : slot {slot} hors plage [{AdditionalProductsConfig.MinSlot}-{AdditionalProductsConfig.MaxSlot}] — ignoré");
                            continue;
                        }

                        string nameField = AdditionalProductsConfig.GetNameField(slot);
                        string valueField = AdditionalProductsConfig.GetValueField(slot);
                        string value;
                        if (IsCurrencyField(service, ref log, item.Line, match.ValueFieldName, moneyFieldCache))
                        {
                            if (!currencySymbolLoaded)
                            {
                                currencySymbol = GetQuoteCurrencySymbol(service, ref log, quote.Id);
                                currencySymbolLoaded = true;
                            }
                            value = FormatCurrencyValue(item.Line, match.ValueFieldName, currencySymbol);
                        }
                        else
                        {
                            value = GetFieldValueAsText(item.Line, match.ValueFieldName);
                        }

                        if (updateEntity.Contains(nameField))
                        {
                            log.Trace($"⚠️ Slot {slot} déjà rempli par '{updateEntity[nameField]}' — écrasé par '{item.LineProductName}'");
                        }

                        // Nom = quotedetail.productname (fallback : nom du produit lié)
                        updateEntity[nameField] = item.LineProductName;
                        updateEntity[valueField] = value;
                        log.Trace($"Slot {slot} → '{item.LineProductName}' = '{value}' (champ source : {match.ValueFieldName})");
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
        /// Retourne les règles applicables au pays de la liste de prix, dans l'ordre du paramétrage.
        /// Les FetchXml sont exécutés ici (un appel par règle FetchXml) ; les règles "ProductNames" ne font aucun appel.
        /// </summary>
        private static List<ResolvedAdditionalProductRule> ResolveAdditionalProductRules(IOrganizationService service, ref TraceLogger log, string countryName, Guid quoteId, Guid? priceLevelId)
        {
            var result = new List<ResolvedAdditionalProductRule>();
            var rules = AdditionalProductsConfig.GetRulesForCountry(countryName);
            log.Trace($"Règles produits additionnels applicables (pays '{countryName ?? "non défini"}') : {rules.Count}");

            foreach (var rule in rules)
            {
                if (!rule.HasNameCriteria && !rule.HasFetchCriteria)
                {
                    log.Trace($"Règle slot {rule.AdditionalProductNum} : ni ProductNames ni FetchXml — ignorée");
                    continue;
                }

                var resolved = new ResolvedAdditionalProductRule { Rule = rule };

                if (rule.HasNameCriteria)
                {
                    log.Trace($"Règle slot {rule.AdditionalProductNum} : critère nom → {string.Join(", ", rule.ProductNames)}");
                }

                if (rule.HasFetchCriteria)
                {
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
                        throw new QuoteTemplateFunctionalException(
                            QuoteTemplateErrors.ParametrageProduitAdditionnel,
                            $"Invalid FetchXml for additional product {rule.AdditionalProductNum} (country '{rule.Country}'): {ex.Message}");
                    }

                    foreach (var record in records.Entities)
                    {
                        var productId = GetProductId(record);
                        if (productId.HasValue)
                            resolved.ProductIds.Add(productId.Value);
                        else
                            log.Trace($"Règle slot {rule.AdditionalProductNum} : résultat '{record.LogicalName}' sans productid — ignoré");
                    }

                    log.Trace($"Règle slot {rule.AdditionalProductNum} : FetchXml → {resolved.ProductIds.Count} produit(s)");
                }

                result.Add(resolved);
            }

            return result;
        }

        /// <summary>
        /// Crée UN SEUL ctx_quotetemplate d'erreur pour le devis :
        /// supprime d'abord les templates existants, puis crée l'enregistrement avec ctx_name = nom de l'erreur
        /// et le message dans le champ d'erreur.
        /// </summary>
        private static void CreateErrorTemplate(IOrganizationService service, ref TraceLogger log, Guid quoteId, string errorName, string errorMessage)
        {
            try
            {
                // Garantir un seul enregistrement : supprimer tout template déjà présent pour ce devis
                var existing = service.RetrieveMultiple(new QueryExpression("ctx_quotetemplate")
                {
                    ColumnSet = new ColumnSet("ctx_quotetemplateid"),
                    Criteria = { Conditions = { new ConditionExpression("ctx_quoteid", ConditionOperator.Equal, quoteId) } }
                }).Entities;

                foreach (var record in existing)
                {
                    service.Delete("ctx_quotetemplate", record.Id);
                }

                string message = errorMessage ?? string.Empty;
                if (message.Length > QuoteTemplateErrors.ErrorMessageMaxLength)
                    message = message.Substring(0, QuoteTemplateErrors.ErrorMessageMaxLength);

                var errorTemplate = new Entity("ctx_quotetemplate");
                errorTemplate["ctx_name"] = errorName;
                errorTemplate["ctx_quoteid"] = new EntityReference("quote", quoteId);
                errorTemplate[QuoteTemplateErrors.ErrorMessageField] = message;

                var id = service.Create(errorTemplate);
                log.Trace($"ctx_quotetemplate d'erreur créé ({id}) : [{errorName}] {message}");
            }
            catch (Exception ex)
            {
                // Impossible d'enregistrer l'erreur (ex : champ absent) → on trace et on sort sans bloquer l'utilisateur
                log.Trace($"Échec de création du ctx_quotetemplate d'erreur : {ex.Message} — erreur d'origine : [{errorName}] {errorMessage}");
            }
        }

        /// <summary>
        /// Libellé d'un champ Choix multiple (ex : "Platform A; Platform B"), lu depuis les FormattedValues.
        /// Fonctionne aussi pour une colonne de lien (clé "alias.champ").
        /// </summary>
        private static string GetMultiSelectLabel(Entity record, string key)
        {
            if (record == null || string.IsNullOrEmpty(key)) return null;

            if (record.FormattedValues.ContainsKey(key) && !string.IsNullOrWhiteSpace(record.FormattedValues[key]))
                return record.FormattedValues[key];

            // Repli : valeurs numériques si le libellé n'est pas disponible
            if (record.Contains(key))
            {
                var raw = record[key];
                if (raw is AliasedValue aliased) raw = aliased.Value;
                if (raw is OptionSetValueCollection values && values.Count > 0)
                    return string.Join("; ", values.Select(v => v.Value.ToString()));
            }
            return null;
        }

        /// <summary>Ajoute une famille (sans doublon) dans ctx_productfamily du template.</summary>
        private static void AddProductFamily(Entity template, string family)
        {
            if (template == null || string.IsNullOrWhiteSpace(family)) return;

            var existing = template.GetAttributeValue<string>(QuoteTemplateFields.ProductFamily);
            var labels = (existing ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();

            foreach (var label in family.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
            {
                if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase)) labels.Add(label);
            }

            template[QuoteTemplateFields.ProductFamily] = string.Join("; ", labels);
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

        /// <summary>
        /// Indique si le champ du quotedetail est de type Currency (Money).
        /// Basé sur les métadonnées (fonctionne aussi quand la valeur est NULL), résultat mis en cache.
        /// </summary>
        private static bool IsCurrencyField(IOrganizationService service, ref TraceLogger log, Entity line, string fieldName, Dictionary<string, bool> cache)
        {
            if (string.IsNullOrWhiteSpace(fieldName)) return false;
            fieldName = fieldName.Trim().ToLowerInvariant();

            // Valeur présente et de type Money → pas besoin des métadonnées
            if (line != null && line.Contains(fieldName))
            {
                var raw = line[fieldName];
                if (raw is AliasedValue aliased) raw = aliased.Value;
                if (raw is Money) return true;
            }

            if (cache.TryGetValue(fieldName, out bool isMoney)) return isMoney;

            try
            {
                var response = (RetrieveAttributeResponse)service.Execute(new RetrieveAttributeRequest
                {
                    EntityLogicalName = "quotedetail",
                    LogicalName = fieldName,
                    RetrieveAsIfPublished = false
                });
                isMoney = response.AttributeMetadata?.AttributeType == AttributeTypeCode.Money;
            }
            catch (Exception ex)
            {
                log.Trace($"Impossible de lire les métadonnées du champ quotedetail.{fieldName} : {ex.Message}");
                isMoney = false;
            }

            cache[fieldName] = isMoney;
            return isMoney;
        }

        /// <summary>Symbole de la devise du devis (quote.transactioncurrencyid → transactioncurrency.currencysymbol).</summary>
        private static string GetQuoteCurrencySymbol(IOrganizationService service, ref TraceLogger log, Guid quoteId)
        {
            try
            {
                var quoteEntity = service.Retrieve("quote", quoteId, new ColumnSet("transactioncurrencyid"));
                var currencyRef = quoteEntity.GetAttributeValue<EntityReference>("transactioncurrencyid");
                if (currencyRef == null)
                {
                    log.Trace("Le devis n'a pas de devise (transactioncurrencyid)");
                    return null;
                }

                var currency = service.Retrieve("transactioncurrency", currencyRef.Id, new ColumnSet("currencysymbol"));
                var symbol = currency.GetAttributeValue<string>("currencysymbol");
                log.Trace($"Symbole de devise du devis : '{symbol}'");
                return symbol;
            }
            catch (Exception ex)
            {
                log.Trace($"Erreur lors de la récupération du symbole de devise : {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Formate un champ Currency : "251,000.00 €" (format #,##0.00, InvariantCulture).
        /// Valeur NULL → "- €".
        /// </summary>
        private static string FormatCurrencyValue(Entity line, string fieldName, string currencySymbol)
        {
            decimal? amount = null;
            fieldName = fieldName?.Trim().ToLowerInvariant();

            if (line != null && !string.IsNullOrEmpty(fieldName) && line.Contains(fieldName) && line[fieldName] != null)
            {
                var raw = line[fieldName];
                if (raw is AliasedValue aliased) raw = aliased.Value;
                if (raw is Money m) amount = m.Value;
                else if (raw is decimal d) amount = d;
            }

            string amountText = amount.HasValue
                ? amount.Value.ToString("#,##0.00", CultureInfo.InvariantCulture)
                : "-";

            return string.IsNullOrEmpty(currencySymbol) ? amountText : $"{amountText} {currencySymbol}";
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
                case Money m: return Math.Round(m.Value, 2).ToString();
                case decimal d: return Math.Round(d, 2).ToString();
                case double db: return Math.Round(db, 2).ToString();
                case OptionSetValue o: return line.FormattedValues.ContainsKey(fieldName) ? line.FormattedValues[fieldName] : o.Value.ToString();
                case EntityReference r: return r.Name ?? r.Id.ToString();
                case bool b: return line.FormattedValues.ContainsKey(fieldName) ? line.FormattedValues[fieldName] : b.ToString();
                default: return raw.ToString();
            }
        }

    }

    /// <summary>Champs liés à la famille de produit.</summary>
    public static class QuoteTemplateFields
    {
        // Nouveau champ texte sur ctx_quotetemplate
        public const string ProductFamily = "ctx_productfamily";

        // Champ Choix multiple sur product
        public const string ProductPlatformList = "ctx_platformlist";

        // Bundles : on ne garde que les produits dont ctx_platformlist contient une de ces valeurs
        public static readonly int[] BundlePlatformListValues = { 961410000, 961410001 };
    }

    /// <summary>Noms des erreurs fonctionnelles (stockés dans ctx_name) + champ du message.</summary>
    public static class QuoteTemplateErrors
    {
        // ⚠️ Nom logique du nouveau champ texte à créer sur ctx_quotetemplate (Plusieurs lignes de texte, 4000)
        public const string ErrorMessageField = "ctx_errormessage";
        public const int ErrorMessageMaxLength = 4000;

        public const string DoublonProduitZone            = "Duplicate product / zone";
        public const string ListePrixManquante            = "Missing price list";
        public const string ParametrageProduitAdditionnel = "Invalid additional product configuration";
    }

    /// <summary>Erreur fonctionnelle : ne bloque pas l'utilisateur, génère un ctx_quotetemplate d'erreur.</summary>
    public class QuoteTemplateFunctionalException : Exception
    {
        public string ErrorName { get; }

        public QuoteTemplateFunctionalException(string errorName, string message) : base(message)
        {
            ErrorName = errorName;
        }
    }
}

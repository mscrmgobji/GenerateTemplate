using System;
using System.Collections.Generic;
using System.Linq;

namespace Centrix.Plugins.BI.Quote.QuoteHandler
{
    /// <summary>
    /// Règle de paramétrage d'un produit additionnel. Deux critères possibles (au moins un des deux) :
    ///   • ProductNames : comparaison simple (sans requête serveur) avec quotedetail.productname
    ///   • FetchXml     : requête retournant des Product (ou des enregistrements portant "productid")
    /// Si les deux sont renseignés, la ligne est retenue si l'un OU l'autre correspond.
    /// Tout quotedetail retenu est affecté au slot ctx_additionalproduct{AdditionalProductNum}.
    /// </summary>
    public class AdditionalProductRule
    {
        /// <summary>Numéro du slot (1 à 5) → ctx_additionalproduct{N}name / ctx_additionalproduct{N}value</summary>
        public int AdditionalProductNum { get; set; }

        /// <summary>Nom du pays de la liste de prix (pricelevel.ctx_countryid). Vide/null = tous les pays.</summary>
        public string Country { get; set; }

        /// <summary>
        /// Critère simple : noms comparés à quotedetail.productname (insensible à la casse).
        /// Aucune requête serveur. Laisser null/vide pour utiliser uniquement le FetchXml.
        /// </summary>
        public List<string> ProductNames { get; set; }

        /// <summary>
        /// Critère avancé : requête FetchXml (texte). Laisser null/vide pour utiliser uniquement ProductNames.
        /// Jetons remplacés à l'exécution :
        ///   {quoteid}      → Id du devis
        ///   {pricelevelid} → Id de la liste de prix du devis
        /// </summary>
        public string FetchXml { get; set; }

        /// <summary>Nom logique du champ du quotedetail dont la valeur est copiée dans ctx_additionalproduct{N}value.</summary>
        public string ValueFieldName { get; set; }

        public bool HasNameCriteria => ProductNames != null && ProductNames.Any(n => !string.IsNullOrWhiteSpace(n));
        public bool HasFetchCriteria => !string.IsNullOrWhiteSpace(FetchXml);

        public bool MatchesProductName(string productName)
        {
            if (!HasNameCriteria || string.IsNullOrWhiteSpace(productName)) return false;
            return ProductNames.Any(n => n != null && n.Trim().Equals(productName.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Règle applicable + GUID des produits retournés par son FetchXml (vide si pas de FetchXml).</summary>
    public class ResolvedAdditionalProductRule
    {
        public AdditionalProductRule Rule { get; set; }
        public HashSet<Guid> ProductIds { get; set; } = new HashSet<Guid>();

        public bool Matches(Guid? productId, string productName)
        {
            if (productId.HasValue && ProductIds.Contains(productId.Value)) return true;
            return Rule.MatchesProductName(productName);
        }
    }

    public static class AdditionalProductsConfig
    {
        // ⚠️ À vérifier : orthographe exacte des champs dans Dataverse (additional vs additionnal)
        public const string NameFieldFormat = "ctx_additionalproduct{0}name";
        public const string ValueFieldFormat = "ctx_additionalproduct{0}value";

        public const int MinSlot = 1;
        public const int MaxSlot = 5;

        public static readonly List<AdditionalProductRule> Rules = new List<AdditionalProductRule>
        {
            // ── Cas avancé : requête FetchXml ──
            new AdditionalProductRule
            {
                AdditionalProductNum = 1,
                Country              = "France",
                ValueFieldName       = "extendedamount",
                FetchXml             =
                  @"<fetch>
                  <entity name='quotedetail'>
                    <filter />
                    <link-entity name='product' from='productid' to='productid' link-type='inner'>
                      <link-entity name='product' from='productid' to='parentproductid'>
                        <filter>
                          <condition attribute='name' operator='eq' value='License' />
                        </filter>
                      </link-entity>
                    </link-entity>
                  </entity>
                </fetch>"
            },
             // ── Cas avancé : requête FetchXml ──
            new AdditionalProductRule
            {
                AdditionalProductNum = 2,
                Country              = "France",
                ValueFieldName       = "extendedamount",
                FetchXml             =
                  @"<fetch>
                  <entity name='quotedetail'>
                    <filter />
                    <link-entity name='product' from='productid' to='productid' link-type='inner'>
                      <link-entity name='product' from='productid' to='parentproductid'>
                        <filter>
                          <condition attribute='name' operator='eq' value='Ongoing Screening Names' />
                        </filter>
                      </link-entity>
                    </link-entity>
                  </entity>
                </fetch>"
            },
            // ── Cas simple : nom du produit (aucune requête serveur) ──
            new AdditionalProductRule
            {
                AdditionalProductNum = 3,
                Country              = "France",
                ProductNames         = new List<string> { "Expert Insight"},
                ValueFieldName       = "extendedamount"
            },
             new AdditionalProductRule
            {
                AdditionalProductNum = 4,
                Country              = "France",
                ProductNames         = new List<string> { "Economic Insight"},
                ValueFieldName       = "extendedamount"
            }

        };

        /// <summary>Règles applicables au pays de la liste de prix (les règles sans pays s'appliquent partout), dans l'ordre déclaré.</summary>
        public static List<AdditionalProductRule> GetRulesForCountry(string country)
        {
            return Rules
                .Where(r => string.IsNullOrWhiteSpace(r.Country)
                         || r.Country.Equals(country ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static string GetNameField(int slot) => string.Format(NameFieldFormat, slot);
        public static string GetValueField(int slot) => string.Format(ValueFieldFormat, slot);

    }
}

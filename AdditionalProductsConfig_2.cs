using System;
using System.Collections.Generic;
using System.Linq;

namespace Centrix.Plugins.BI.Quote.QuoteHandler
{
    /// <summary>
    /// Règle de paramétrage d'un produit additionnel.
    /// La requête FetchXml doit retourner des Product (ou des enregistrements portant un attribut "productid").
    /// Tout quotedetail dont le productid figure dans ce résultat est considéré comme produit additionnel
    /// et est affecté au slot ctx_additionalproduct{AdditionalProductNum}.
    /// </summary>
    public class AdditionalProductRule
    {
        /// <summary>Numéro du slot (1 à 5) → ctx_additionalproduct{N}name / ctx_additionalproduct{N}value</summary>
        public int AdditionalProductNum { get; set; }

        /// <summary>Nom du pays de la liste de prix (pricelevel.ctx_countryid). Vide/null = tous les pays.</summary>
        public string Country { get; set; }

        /// <summary>
        /// Requête FetchXml (texte). Jetons remplacés à l'exécution :
        ///   {quoteid}      → Id du devis
        ///   {pricelevelid} → Id de la liste de prix du devis
        /// </summary>
        public string FetchXml { get; set; }

        /// <summary>Nom logique du champ du quotedetail dont la valeur est copiée dans ctx_additionalproduct{N}value.</summary>
        public string ValueFieldName { get; set; }
    }

    /// <summary>Résultat de l'évaluation des requêtes : un produit identifié comme additionnel.</summary>
    public class AdditionalProductMatch
    {
        public Guid ProductId { get; set; }
        public int AdditionalProductNum { get; set; }
        public string ValueFieldName { get; set; }
    }

    public static class AdditionalProductsConfig
    {
        // ⚠️ À vérifier : orthographe exacte des champs dans Dataverse (additional vs additionnal)
        public const string NameFieldFormat  = "ctx_additionalproduct{0}name";
        public const string ValueFieldFormat = "ctx_additionalproduct{0}value";

        public const int MinSlot = 1;
        public const int MaxSlot = 5;

        public static readonly List<AdditionalProductRule> Rules = new List<AdditionalProductRule>
        {
            new AdditionalProductRule
            {
                AdditionalProductNum = 1,
                Country              = "France",
                ValueFieldName       = "ctx_unitpriceafterdiscount",
                FetchXml             =
                    "<fetch top=\"1\">" +
                    "  <entity name=\"quotedetail\">" +
                    "    <filter>" +
                    "      <condition attribute=\"productid\" operator=\"eq\" value=\"30ad38f2-015e-f111-a826-002248a26111\" />" +
                    "    </filter>" +
                    "    <link-entity name=\"product\" from=\"productid\" to=\"productid\" link-type=\"inner\">" +
                    "      <link-entity name=\"product\" from=\"productid\" to=\"parentproductid\">" +
                    "        <filter>" +
                    "          <condition attribute=\"name\" operator=\"eq\" value=\"License\" />" +
                    "        </filter>" +
                    "      </link-entity>" +
                    "    </link-entity>" +
                    "  </entity>" +
                    "</fetch>"
            },
            // new AdditionalProductRule
            // {
            //     AdditionalProductNum = 2,
            //     Country              = "France",
            //     ValueFieldName       = "ctx_unitpriceafterdiscount",
            //     FetchXml             = "<fetch><entity name=\"product\"><attribute name=\"productid\" />...</entity></fetch>"
            // },
        };

        /// <summary>Règles applicables au pays de la liste de prix (les règles sans pays s'appliquent partout).</summary>
        public static List<AdditionalProductRule> GetRulesForCountry(string country)
        {
            return Rules
                .Where(r => string.IsNullOrWhiteSpace(r.Country)
                         || r.Country.Equals(country ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static string GetNameField(int slot)  => string.Format(NameFieldFormat, slot);
        public static string GetValueField(int slot) => string.Format(ValueFieldFormat, slot);
    }
}

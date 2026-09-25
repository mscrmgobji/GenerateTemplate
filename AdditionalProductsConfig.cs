using System;
using System.Collections.Generic;
using System.Linq;

namespace Centrix.Plugins.BI.Quote.QuoteHandler
{
    /// <summary>
    /// Règle de mapping : un produit additionnel est affecté à un slot ctx_additionalproductN
    /// selon son nom ET le pays du propriétaire du devis.
    /// </summary>
    public class AdditionalProductRule
    {
        public int AdditionnalProductNum { get; set; }

        
        public List<string> ProductNames { get; set; }

        public List<string> Countries { get; set; }
    }
    public static class AdditionalProductsConfig
    {
        public static readonly List<AdditionalProductRule> Rules = new List<AdditionalProductRule>
        {
            new AdditionalProductRule
            {
                AdditionnalProductNum         = 1,
                ProductNames = new List<string> { "Expert Insight" },
                Countries    = new List<string> { "France" }
            },
            new AdditionalProductRule
            {
                AdditionnalProductNum         = 2,
                ProductNames = new List<string> { "Compliance Screening - License" },
                Countries    = new List<string> { "France" }
            },
            new AdditionalProductRule
            {
                AdditionnalProductNum         = 3,
                ProductNames = new List<string> { "Compliance Screening - Monitoring" },
                Countries    = new List<string> { "France" }
            },
            new AdditionalProductRule
            {
                AdditionnalProductNum         = 4,
                ProductNames = new List<string> { "Economic Insight" },
                Countries    = new List<string> { "France"}
            },
        };

        public static bool IsAdditionalProduct(string productName)
        {
            if (string.IsNullOrEmpty(productName)) return false;
            return Rules.Any(r =>
                r.ProductNames.Any(n => n.Equals(productName, StringComparison.OrdinalIgnoreCase)));
        }

        public static int? GetSlot(string productName, string country)
        {
            if (string.IsNullOrEmpty(productName)) return null;

            var rule = Rules.FirstOrDefault(r =>
                r.ProductNames.Any(n => n.Equals(productName, StringComparison.OrdinalIgnoreCase))
                &&
                (
                    r.Countries == null
                    || r.Countries.Count == 0
                    || r.Countries.Any(c => c.Equals(country ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ));

            return rule?.AdditionnalProductNum;
        }
    }
}
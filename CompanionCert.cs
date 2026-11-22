using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IslandPlayer
{
    /// <summary>
    /// Własny certyfikat dla aplikacji w telefonie. Dzięki niemu strona idzie przez https, a tylko wtedy
    /// telefon może ją zapisać i otwierać także przy wyłączonym komputerze (service worker).
    ///
    /// Główny certyfikat "Isabelle" instaluje się raz w telefonie. Jest ograniczony (Name Constraints) tylko do
    /// adresów w domowej sieci (*.local, 192.168.x.x, 10.x.x.x, 172.16-31.x.x), więc nawet gdyby ktoś ukradł
    /// klucz z komputera, nie podrobi nim żadnej prawdziwej strony internetowej.
    /// </summary>
    public static class CompanionCert
    {
        private static string CaPath => Path.Combine(Settings.Folder, "phone-ca.pfx");
        private static string ServerPath => Path.Combine(Settings.Folder, "phone-server.pfx");
        private static X509Certificate2 _server;

        /// <summary>Nazwa komputera w sieci (np. "pokoj-pc.local") - stała nawet gdy zmieni się adres IP.</summary>
        public static string HostName
        {
            get
            {
                string h = new string((Environment.MachineName ?? "isabelle").ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
                return (h.Length > 0 ? h : "isabelle") + ".local";
            }
        }

        /// <summary>Główny certyfikat do zainstalowania w telefonie (tylko część publiczna, plik .cer).</summary>
        public static byte[] CaDer()
        {
            using var ca = LoadOrCreateCa();
            return ca.Export(X509ContentType.Cert);
        }

        /// <summary>Certyfikat serwera https. Odnawiany sam, gdy zmieni się adres IP albo zbliża się koniec ważności.</summary>
        public static X509Certificate2 Server()
        {
            var ips = LanIps();
            if (_server != null && Covers(_server, ips) && _server.NotAfter > DateTime.Now.AddDays(30)) return _server;
            lock (typeof(CompanionCert))
            {
                if (_server != null && Covers(_server, ips) && _server.NotAfter > DateTime.Now.AddDays(30)) return _server;
                try
                {
                    if (File.Exists(ServerPath))
                    {
                        var c = Import(File.ReadAllBytes(ServerPath));
                        if (Covers(c, ips) && c.NotAfter > DateTime.Now.AddDays(30)) return _server = c;
                    }
                }
                catch (Exception ex) { Log.Info("telefon cert: " + ex.Message); }

                using var ca = LoadOrCreateCa();
                using var key = RSA.Create(2048);
                var req = new CertificateRequest("CN=" + HostName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(HostName);
                foreach (var ip in ips) san.AddIpAddress(ip);
                req.CertificateExtensions.Add(san.Build());
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                // iPhone przyjmuje certyfikaty serwera ważne najwyżej 825 dni
                var serial = new byte[16]; RandomNumberGenerator.Fill(serial); serial[0] &= 0x7F;
                using var signed = req.Create(ca, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(800), serial);
                using var withKey = signed.CopyWithPrivateKey(key);
                var pfx = withKey.Export(X509ContentType.Pfx);
                File.WriteAllBytes(ServerPath, pfx);
                _server?.Dispose();
                return _server = Import(pfx);
            }
        }

        // ================= wnętrze =================
        private static X509Certificate2 LoadOrCreateCa()
        {
            try { if (File.Exists(CaPath)) return new X509Certificate2(File.ReadAllBytes(CaPath), (string)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet); }
            catch (Exception ex) { Log.Info("telefon CA: " + ex.Message); }

            using var key = RSA.Create(3072);
            var req = new CertificateRequest("CN=Isabelle (" + Environment.MachineName + "), O=Isabelle", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            req.CertificateExtensions.Add(NameConstraints());
            using var ca = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(15));
            var pfx = ca.Export(X509ContentType.Pfx);
            File.WriteAllBytes(CaPath, pfx);
            try { File.Delete(ServerPath); } catch { }
            return new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }

        /// <summary>Name Constraints: CA może podpisywać tylko adresy z domowej sieci.</summary>
        private static X509Extension NameConstraints()
        {
            var w = new AsnWriter(AsnEncodingRules.DER);
            using (w.PushSequence())                                           // NameConstraints
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))  // permittedSubtrees
            {
                using (w.PushSequence())                                       // GeneralSubtree: dNSName "local"
                    w.WriteCharacterString(UniversalTagNumber.IA5String, "local", new Asn1Tag(TagClass.ContextSpecific, 2));
                foreach (var (net, mask) in new[] { ("192.168.0.0", "255.255.0.0"), ("10.0.0.0", "255.0.0.0"), ("172.16.0.0", "255.240.0.0") })
                {
                    using (w.PushSequence())                                   // GeneralSubtree: iPAddress (adres + maska)
                        w.WriteOctetString(IPAddress.Parse(net).GetAddressBytes().Concat(IPAddress.Parse(mask).GetAddressBytes()).ToArray(), new Asn1Tag(TagClass.ContextSpecific, 7));
                }
            }
            return new X509Extension(new Oid("2.5.29.30"), w.Encode(), true);
        }

        private static X509Certificate2 Import(byte[] pfx) =>
            // SChannel (SslStream na Windows) potrzebuje klucza zapisanego, nie tylko w pamięci
            new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

        private static bool Covers(X509Certificate2 c, List<IPAddress> ips)
        {
            var ext = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            if (ext == null) return false;
            var have = ext.EnumerateIPAddresses().ToList();
            return ext.EnumerateDnsNames().Contains(HostName) && ips.All(ip => have.Any(h => h.Equals(ip)));
        }

        /// <summary>Adresy komputera w domowej sieci (tylko prywatne - takie dopuszcza certyfikat główny).</summary>
        public static List<IPAddress> LanIps()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = a.Address.GetAddressBytes();
                        bool lan = b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
                        if (lan && !list.Contains(a.Address)) list.Add(a.Address);
                    }
                }
            }
            catch { }
            return list;
        }
    }
}

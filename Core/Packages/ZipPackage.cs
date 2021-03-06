using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGetPe
{
    public class ZipPackage : IPackage, IDisposable
    {
        private const string AssemblyReferencesDir = "lib";
        private const string ResourceAssemblyExtension = ".resources.dll";
        private static readonly string[] AssemblyReferencesExtensions = new[] {".dll", ".exe", ".winmd"};

        // paths to exclude
        private static readonly string[] ExcludePaths = new[] {"_rels", "package","[Content_Types]", ".signature"};

        // We don't store the steam itself, just a way to open the stream on demand
        // so we don't have to hold on to that resource
        private readonly Func<Stream> _streamFactory;
        private ManifestMetadata metadata;

        public ZipPackage(string filePath)
        {
            if (String.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("Argument cannot be null.", "filePath");
            }

            if (!File.Exists(filePath))
            {
                throw new ArgumentException("File doesn't exist at '" + filePath + "'.", "filePath");
            }

            Source = filePath;
            _streamFactory = () =>
            {
                try
                {
                    return File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                }
                catch (UnauthorizedAccessException)
                {
                    //just try read
                    return File.Open(filePath, FileMode.Open,FileAccess.Read);
                }

            };
            EnsureManifest();
        }

        public string Source { get; }

        #region IPackage Members

        public string Id
        {
            get { return metadata.Id; }
            set { metadata.Id = value; }
        }

        public NuGetVersion Version
        {
            get { return metadata.Version; }
            set { metadata.Version = value; }
        }

        public string Title
        {
            get { return metadata.Title; }
            set { metadata.Title = value; }
        }

        public IEnumerable<string> Authors
        {
            get { return metadata.Authors; }
            set { metadata.Authors = value; }
        }

        public IEnumerable<string> Owners
        {
            get { return metadata.Owners; }
            set { metadata.Owners = value; }
        }

        public Uri IconUrl
        {
            get { return metadata.IconUrl; }
            set { metadata.SetIconUrl(value?.ToString()); }
        }

        public Uri LicenseUrl
        {
            get { return metadata.LicenseUrl; }
            set { metadata.SetLicenseUrl(value?.ToString()); }
        }

        public Uri ProjectUrl
        {
            get { return metadata.ProjectUrl; }
            set { metadata.SetProjectUrl(value?.ToString()); }
        }

        public bool RequireLicenseAcceptance
        {
            get { return metadata.RequireLicenseAcceptance; }
            set { metadata.RequireLicenseAcceptance = value; }
        }

        public bool DevelopmentDependency
        {
            get { return metadata.DevelopmentDependency; }
            set { metadata.DevelopmentDependency = value; }
        }

        public string Description
        {
            get { return metadata.Description; }
            set { metadata.Description = value; }
        }

        public string Summary
        {
            get { return metadata.Summary; }
            set { metadata.Summary = value; }
        }

        public string ReleaseNotes
        {
            get { return metadata.ReleaseNotes; }
            set { metadata.ReleaseNotes = value; }
        }

        public string Language
        {
            get { return metadata.Language; }
            set { metadata.Language = value; }
        }

        public string Tags
        {
            // Ensure tags start and end with an empty " " so we can do contains filtering reliably
            get { return !string.IsNullOrWhiteSpace(metadata.Tags) ? $" {metadata.Tags} " : metadata.Tags; }
            set { metadata.Tags = value?.Trim(); }
        }

        public bool Serviceable
        {
            get { return metadata.Serviceable; }
            set { metadata.Serviceable = value; }
        }

        public string Copyright
        {
            get { return metadata.Copyright; }
            set { metadata.Copyright = value; }
        }

        public Version MinClientVersion
        {
            get { return metadata.MinClientVersion; }
            set { metadata.MinClientVersionString = value?.ToString(); }
        }

        public IEnumerable<PackageDependencyGroup> DependencyGroups
        {
            get { return metadata.DependencyGroups; }
            set { metadata.DependencyGroups = value; }
        }

        public IEnumerable<PackageReferenceSet> PackageAssemblyReferences
        {
            get { return metadata.PackageAssemblyReferences; }
            set { metadata.PackageAssemblyReferences = value; }
        }

        public IEnumerable<FrameworkAssemblyReference> FrameworkReferences
        {
            get { return metadata.FrameworkReferences; }
            set { metadata.FrameworkReferences = value; }
        }

        public IEnumerable<ManifestContentFiles> ContentFiles
        {
            get { return metadata.ContentFiles; }
            set { metadata.ContentFiles = value; }
        }

        public IEnumerable<PackageType> PackageTypes
        {
            get { return metadata.PackageTypes; }
            set { metadata.PackageTypes = value; }
        }

        public RepositoryMetadata Repository
        {
            get { return metadata.Repository; }
            set { metadata.Repository = value; }
        }

        public DateTimeOffset? Published
        {
            get;
            set;
        }

        public Uri ReportAbuseUrl
        {
            get { return null; }
        }

        public int DownloadCount
        {
            get { return 0; }
        }

        public int VersionDownloadCount
        {
            get { return 0; }
        }

        public bool IsAbsoluteLatestVersion
        {
            get { return true; }
        }

        public bool IsLatestVersion
        {
            get { return this.IsReleaseVersion(); }
        }

        private DateTimeOffset? _lastUpdated;
        public DateTimeOffset LastUpdated
        {
            get
            {
                if (_lastUpdated == null)
                {
                    _lastUpdated = File.GetLastWriteTimeUtc(Source);
                }
                return _lastUpdated.Value;
            }
        }

        private long? _packageSize;
        public long PackageSize
        {
            get
            {
                if (_packageSize == null)
                {
                    _packageSize = new FileInfo(Source).Length;
                }
                return _packageSize.Value;
            }
        }

        public string PackageHash
        {
            get { return null; }
        }

        public bool IsPrerelease
        {
            get
            {
                return Version.IsPrerelease;
            }
        }

        public bool IsSigned { get; private set; }

        public bool IsVerified => false;

        public X509Certificate2 PublisherCertificate { get; private set; }

        public X509Certificate2 RepositoryCertificate => null;


        // Keep a list of open stream here, and close on dispose.
        private List<IDisposable> _danglingStreams = new List<IDisposable>();

        public IEnumerable<IPackageFile> GetFiles()
        {
            Stream stream = _streamFactory();
            var reader = new PackageArchiveReader(stream, false); // should not close
           
            _danglingStreams.Add(reader);           // clean up on dispose

            
            return (from file in reader.GetFiles()
                    where IsPackageFile(file, reader)
                    select new ZipPackageFile(reader, file)).ToList();
        }

        public Stream GetStream()
        {
            return _streamFactory();
        }

        #endregion

        private void EnsureManifest()
        {
            using (Stream stream = _streamFactory())
            using (var reader = new PackageArchiveReader(stream))
            {
                var manifest = Manifest.ReadFrom(reader.GetNuspec(), false);
                metadata = manifest.Metadata;
            }
        }

        private bool IsPackageFile(string path, PackageArchiveReader reader)
        {
            // We exclude any opc files and the manifest file (.nuspec)

            // check for signature here as a hack until we have API support in nuget
            if (path.StartsWith(".signature"))
            {
                IsSigned = true;
                using (var ms = new MemoryStream())
                using (var str = reader.GetStream(path))
                {
                    str.CopyTo(ms);
                    PopulateSignatureProperties(ms.ToArray());
                }
                   
            }

            return !path.EndsWith("/") && !ExcludePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                   !PackageUtility.IsManifest(path);
        }

        private void PopulateSignatureProperties(byte[] messageBytes)
        {
            var signedCms = new SignedCms();
            signedCms.Decode(messageBytes);
            var signerInfo = signedCms.SignerInfos[0]; // Should always be 1 total

            PublisherCertificate = signerInfo.Certificate;
        }

        public override string ToString()
        {
            return this.GetFullName();
        }

        public void Dispose()
        {
            _danglingStreams.ForEach(ds => ds.Dispose());
        }
    }
}
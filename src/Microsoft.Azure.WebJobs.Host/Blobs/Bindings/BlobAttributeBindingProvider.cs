// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Converters;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Indexers;
using Microsoft.Azure.WebJobs.Host.Storage;
using Microsoft.Azure.WebJobs.Host.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Microsoft.Azure.WebJobs.Host.Blobs.Bindings
{
    internal class BlobAttributeBindingProvider : IBindingProvider
    {
        private readonly INameResolver _nameResolver;
        private readonly IStorageAccountProvider _accountProvider;
        private readonly IBlobArgumentBindingProvider _blobArgumentProvider;
        private readonly IBlobContainerArgumentBindingProvider _blobContainerArgumentProvider;

        public BlobAttributeBindingProvider(INameResolver nameResolver, IStorageAccountProvider accountProvider,
            IExtensionTypeLocator extensionTypeLocator, IContextGetter<IBlobWrittenWatcher> blobWrittenWatcherGetter)
        {
            if (accountProvider == null)
            {
                throw new ArgumentNullException("accountProvider");
            }

            if (extensionTypeLocator == null)
            {
                throw new ArgumentNullException("extensionTypeLocator");
            }

            if (blobWrittenWatcherGetter == null)
            {
                throw new ArgumentNullException("blobWrittenWatcherGetter");
            }

            _nameResolver = nameResolver;
            _accountProvider = accountProvider;
            _blobArgumentProvider = CreateBlobArgumentProvider(extensionTypeLocator.GetCloudBlobStreamBinderTypes(), blobWrittenWatcherGetter);
            _blobContainerArgumentProvider = CreateBlobContainerArgumentProvider(extensionTypeLocator.GetCloudBlobStreamBinderTypes());
        }

        public async Task<IBinding> TryCreateAsync(BindingProviderContext context)
        {
            ParameterInfo parameter = context.Parameter;
            BlobAttribute blobAttribute = parameter.GetCustomAttribute<BlobAttribute>(inherit: false);

            if (blobAttribute == null)
            {
                return null;
            }

            string resolvedPath = Resolve(blobAttribute.BlobPath);
            IBindableBlobPath path = null;
            IStorageAccount account = await _accountProvider.GetStorageAccountAsync(context.Parameter, context.CancellationToken);
            StorageClientFactoryContext clientFactoryContext = new StorageClientFactoryContext
            {
                Parameter = context.Parameter
            };
            IStorageBlobClient client = account.CreateBlobClient(clientFactoryContext);

            // trying to fix improper container binding


            // first try to bind to the Container
            Trace.TraceInformation("_blobContainerArgumentProvider Type: '" + _blobContainerArgumentProvider.GetType().Name + "'.");
            IArgumentBinding<IStorageBlobContainer> containerArgumentBinding = _blobContainerArgumentProvider.TryCreate(parameter);

            //BIND_AS_NOT_CONTAINER:
            if (containerArgumentBinding == null || Regex.IsMatch(resolvedPath, @"\.[^/]+$"))
            {
                // if this isn't a Container binding, try a Blob binding
                IBlobArgumentBinding blobArgumentBinding = _blobArgumentProvider.TryCreate(parameter, blobAttribute.Access);
                if (blobArgumentBinding == null)
                {
                    throw new InvalidOperationException("Can't bind Blob to type '" + parameter.ParameterType + "' (BlobArgumentProvider Type '" + _blobArgumentProvider.GetType().Name + "').");
                }

                path = BindableBlobPath.Create(resolvedPath);
                path.ValidateContractCompatibility(context.BindingDataContract);

                return new BlobBinding(parameter.Name, blobArgumentBinding, client, path);
            }

            path = BindableBlobPath.Create(resolvedPath, isContainerBinding: true);
            path.ValidateContractCompatibility(context.BindingDataContract);
            BlobContainerBinding.ValidateContainerBinding(blobAttribute, parameter.ParameterType, path);

            /*
             *** This try/catch checks if a blob exists for the given container + path.
             *** If the blob does exist, then this should not be a container binding.
             *** HOWEVER, we only have container and path name PATTERNS, not the actual values.
             *** As an alternative for now, I'll just check to see if the path name pattern
             *** ends with a file extension (added to line 82).
            try
            {
                var container = client.GetContainerReference(path.ContainerNamePattern);
                var blobRef =
                    await container.GetBlobReferenceFromServerAsync(path.BlobNamePattern, CancellationToken.None);
                var exists = await blobRef.ExistsAsync(CancellationToken.None);
                if (exists)
                {
                    containerArgumentBinding = null;
                    goto BIND_AS_NOT_CONTAINER;
                }
            }
            catch (Exception ex)
            {
                var webException = ex.InnerException as WebException;
                if (webException == null)
                    throw;
                var httpWebResponse = webException.Response as HttpWebResponse;
                if (httpWebResponse == null || httpWebResponse.StatusCode != HttpStatusCode.NotFound)
                    throw;
            }
            */

            return new BlobContainerBinding(parameter.Name, containerArgumentBinding, client, path);
        }

        private string Resolve(string blobName)
        {
            if (_nameResolver == null)
            {
                return blobName;
            }

            return _nameResolver.ResolveWholeString(blobName);
        }

        private static IBlobArgumentBindingProvider CreateBlobArgumentProvider(IEnumerable<Type> cloudBlobStreamBinderTypes,
            IContextGetter<IBlobWrittenWatcher> blobWrittenWatcherGetter)
        {
            List<IBlobArgumentBindingProvider> innerProviders = new List<IBlobArgumentBindingProvider>();

            innerProviders.Add(CreateConverterProvider<ICloudBlob, StorageBlobToCloudBlobConverter>());
            innerProviders.Add(CreateConverterProvider<CloudBlockBlob, StorageBlobToCloudBlockBlobConverter>());
            innerProviders.Add(CreateConverterProvider<CloudPageBlob, StorageBlobToCloudPageBlobConverter>());
            innerProviders.Add(new StreamArgumentBindingProvider(blobWrittenWatcherGetter));
            innerProviders.Add(new CloudBlobStreamArgumentBindingProvider(blobWrittenWatcherGetter));
            innerProviders.Add(new TextReaderArgumentBindingProvider());
            innerProviders.Add(new TextWriterArgumentBindingProvider(blobWrittenWatcherGetter));
            innerProviders.Add(new StringArgumentBindingProvider());
            innerProviders.Add(new OutStringArgumentBindingProvider(blobWrittenWatcherGetter));

            if (cloudBlobStreamBinderTypes != null)
            {
                innerProviders.AddRange(cloudBlobStreamBinderTypes.Select(
                    t => CloudBlobStreamObjectBinder.CreateReadBindingProvider(t)));

                innerProviders.AddRange(cloudBlobStreamBinderTypes.Select(
                    t => CloudBlobStreamObjectBinder.CreateWriteBindingProvider(t, blobWrittenWatcherGetter)));
            }

            return new CompositeBlobArgumentBindingProvider(innerProviders);
        }

        private static IBlobContainerArgumentBindingProvider CreateBlobContainerArgumentProvider(IEnumerable<Type> cloudBlobStreamBinderTypes)
        {
            List<IBlobContainerArgumentBindingProvider> innerProviders = new List<IBlobContainerArgumentBindingProvider>();

            innerProviders.Add(new CloudBlobContainerArgumentBindingProvider());
            innerProviders.Add(new CloudBlobDirectoryArgumentBindingProvider());
            innerProviders.Add(new CloudBlobEnumerableArgumentBindingProvider());

            if (cloudBlobStreamBinderTypes != null)
            {
                innerProviders.AddRange(cloudBlobStreamBinderTypes.Select(
                    t => CloudBlobStreamObjectBinder.CreateContainerReadBindingProvider(t)));
            }

            return new CompositeBlobContainerArgumentBindingProvider(innerProviders);
        }

        private static IBlobArgumentBindingProvider CreateConverterProvider<TValue, TConverter>()
            where TConverter : IConverter<IStorageBlob, TValue>, new()
        {
            return new ConverterArgumentBindingProvider<TValue>(new TConverter());
        }
    }
}

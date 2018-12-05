﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using Examine.LuceneEngine.Directories;
using Examine.LuceneEngine.Indexing;
using Examine.Providers;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Directory = Lucene.Net.Store.Directory;
using Version = Lucene.Net.Util.Version;


namespace Examine.LuceneEngine.Providers
{
    ///<summary>
    /// Abstract object containing all of the logic used to use Lucene as an indexer
    ///</summary>
    public class LuceneIndex : BaseIndexProvider, IDisposable
    {
        #region Constructors

        /// <summary>
        /// Constructor used for provider instantiation
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public LuceneIndex()
        {
            _disposer = new DisposableIndex(this);
            _committer = new IndexCommiter(this);
            _searcher = new Lazy<LuceneSearcher>(CreateSearcher);
            WaitForIndexQueueOnShutdown = true;
        }

        /// <summary>
        /// Constructor to create an indexer at runtime
        /// </summary>
        /// <param name="name"></param>
        /// <param name="fieldDefinitions"></param>
        /// <param name="validator">A custom validator used to validate a value set before it can be indexed</param>
        /// <param name="luceneDirectory"></param>
        /// <param name="analyzer">Specifies the default analyzer to use per field</param>
        /// <param name="indexValueTypesFactory">
        /// Specifies the index value types to use for this indexer, if this is not specified then the result of <see cref="DefaultIndexValueTypes"/> will be used.
        /// This is generally used to initialize any custom value types for your indexer since the value type collection cannot be modified at runtime.
        /// </param>
        public LuceneIndex(
            string name, 
            IEnumerable<FieldDefinition> fieldDefinitions,
            Directory luceneDirectory,
            Analyzer analyzer,
            IValueSetValidator validator = null,
            IReadOnlyDictionary<string, Func<string, IIndexValueType>> indexValueTypesFactory = null)
            : base(name, fieldDefinitions, validator)
        {
            _disposer = new DisposableIndex(this);
            _committer = new IndexCommiter(this);

            LuceneIndexFolder = null;

            DefaultAnalyzer = analyzer;

            _directory = luceneDirectory;
            //initialize the field types
            _fieldValueTypeCollection = new Lazy<FieldValueTypeCollection>(() => CreateFieldValueTypes(indexValueTypesFactory));
            _searcher = new Lazy<LuceneSearcher>(CreateSearcher);
            WaitForIndexQueueOnShutdown = true;
        }

        //TODO: The problem with this is that the writer would already need to be configured with a PerFieldAnalyzerWrapper
        // with all of the field definitions in place, etc... but that will most likely never happen
        /// <summary>
        /// Constructor to allow for creating an indexer at runtime - using NRT
        /// </summary>
        /// <param name="name"></param>
        /// <param name="fieldDefinitions"></param>
        /// <param name="writer"></param>
        /// <param name="validator"></param>
        /// <param name="indexValueTypesFactory"></param>
        internal LuceneIndex(
            string name,
            IEnumerable<FieldDefinition> fieldDefinitions,
            IndexWriter writer,
            IValueSetValidator validator = null,
            IReadOnlyDictionary<string, Func<string, IIndexValueType>> indexValueTypesFactory = null)
            : base(name, fieldDefinitions, validator)
        {
            _disposer = new DisposableIndex(this);
            _committer = new IndexCommiter(this);

            DefaultAnalyzer = writer.Analyzer;
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));

            //initialize the field types
            _fieldValueTypeCollection = new Lazy<FieldValueTypeCollection>(() => CreateFieldValueTypes(indexValueTypesFactory));
            LuceneIndexFolder = null;
            _searcher = new Lazy<LuceneSearcher>(CreateSearcher);
            WaitForIndexQueueOnShutdown = true;
        }

        #endregion

        #region Initialize

        /// <summary>
        /// Set up all properties for the indexer based on configuration information specified. This will ensure that
        /// all of the folders required by the indexer are created and exist. This will also create an instruction
        /// file declaring the computer name that is part taking in the indexing. This file will then be used to
        /// determine the master indexer machine in a load balanced environment (if one exists).
        /// </summary>
        /// <param name="name">The friendly name of the provider.</param>
        /// <param name="config">A collection of the name/value pairs representing the provider-specific attributes specified in the configuration for this provider.</param>
        /// <exception cref="T:System.ArgumentNullException">
        /// The name of the provider is null.
        /// </exception>
        /// <exception cref="T:System.ArgumentException">
        /// The name of the provider has a length of zero.
        /// </exception>
        /// <exception cref="T:System.InvalidOperationException">
        /// An attempt is made to call <see cref="M:System.Configuration.Provider.ProviderBase.Initialize(System.String,System.Collections.Specialized.NameValueCollection)"/> on a provider after the provider has already been initialized.
        /// </exception>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override void Initialize(string name, NameValueCollection config)
        {
            base.Initialize(name, config);

            if (config["directoryFactory"] != null)
            {
                //this should be a fully qualified type
                var factoryType = TypeHelper.FindType(config["directoryFactory"]);
                if (factoryType == null) throw new NullReferenceException("No directory type found for value: " + config["directoryFactory"]);
                DirectoryFactory = (IDirectoryFactory)Activator.CreateInstance(factoryType);
            }

            if (config["analyzer"] != null)
            {
                //this should be a fully qualified type
                var analyzerType = TypeHelper.FindType(config["analyzer"]);
                if (typeof(StandardAnalyzer).IsAssignableFrom(analyzerType))
                    DefaultAnalyzer = (Analyzer)Activator.CreateInstance(analyzerType, Version.LUCENE_30);
                else
                    DefaultAnalyzer = (Analyzer)Activator.CreateInstance(analyzerType);
            }
            else
            {
                DefaultAnalyzer = new CultureInvariantStandardAnalyzer(Version.LUCENE_30);
            }

            if (config["indexFolder"] != null)
            {
                LuceneIndexFolder = new DirectoryInfo(
                    IOHelper.MapPath(
                        IOHelper.ReplaceTokensInIndexPath(
                            config["indexFolder"])));
            }

            _directory = InitializeDirectory();

            _fieldValueTypeCollection = new Lazy<FieldValueTypeCollection>(() => CreateFieldValueTypes());

            CommitCount = 0;
        }

        #endregion

        #region Constants & Fields

        private volatile IndexWriter _writer;

        private int _activeWrites = 0;
        private int _activeAddsOrDeletes = 0;

        /// <summary>
        /// The prefix characters denoting a special field stored in the lucene index for use internally
        /// </summary>
        public const string SpecialFieldPrefix = "__";

        /// <summary>
        /// The prefix added to a field when it is included in the index for sorting
        /// </summary>
        public const string SortedFieldNamePrefix = "__Sort_";

        /// <summary>
        /// Used to store a non-tokenized key for the document for the Category
        /// </summary>
        public const string CategoryFieldName = "__IndexType";

        /// <summary>
        /// Used to store a non-tokenized type for the document
        /// </summary>
        public const string ItemIdFieldName = "__NodeId";

        public const string ItemTypeFieldName = "__NodeTypeAlias";

        /// <summary>
        /// Used to perform thread locking
        /// </summary>
        private readonly object _indexingLocker = new object();

        /// <summary>
        /// Used to aquire the index writer
        /// </summary>
        private readonly object _writerLocker = new object();

        /// <summary>
        /// used to thread lock calls for creating and verifying folders
        /// </summary>
        private readonly object _folderLocker = new object();

        /// <summary>
        /// Used for double check locking during an index operation
        /// </summary>
        private volatile bool _isIndexing = false;

        private readonly Lazy<LuceneSearcher> _searcher;

        private bool? _exists;

        /// <summary>
        /// Gets a searcher for the index
        /// </summary>
        public override ISearcher GetSearcher()
        {
            return _searcher.Value;
        }

        /// <summary>
        /// This is our threadsafe queue of items which can be read by our background worker to process the queue
        /// </summary>
        /// <remarks>
        /// Each item in the collection is a collection itself, this allows us to have lazy access to a collection as part of the queue if added in bulk
        /// </remarks>
        private readonly BlockingCollection<IEnumerable<IndexOperation>> _indexQueue = new BlockingCollection<IEnumerable<IndexOperation>>();

        /// <summary>
        /// The async task that runs during an async indexing operation
        /// </summary>
        private Task _asyncTask;

        /// <summary>
        /// Used to cancel the async operation
        /// </summary>
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        #endregion

        #region Properties

        private Lazy<FieldValueTypeCollection> _fieldValueTypeCollection;

        /// <summary>
        /// Returns the <see cref="FieldValueTypeCollection"/> configured for this index
        /// </summary>
        public FieldValueTypeCollection FieldValueTypeCollection => _fieldValueTypeCollection.Value;

        /// <summary>
        /// Returns the default index value types that is used in normal construction of an indexer
        /// </summary>
        /// <returns></returns>
        public static IReadOnlyDictionary<string, Func<string, IIndexValueType>> DefaultIndexValueTypes
            => new Dictionary<string, Func<string, IIndexValueType>>(StringComparer.InvariantCultureIgnoreCase) //case insensitive
            {
                {"number", name => new Int32Type(name)},
                {FieldDefinitionTypes.Integer, name => new Int32Type(name)},
                {FieldDefinitionTypes.Float, name => new SingleType(name)},
                {FieldDefinitionTypes.Double, name => new DoubleType(name)},
                {FieldDefinitionTypes.Long, name => new Int64Type(name)},
                {"date", name => new DateTimeType(name, DateTools.Resolution.MILLISECOND)},
                {FieldDefinitionTypes.DateTime, name => new DateTimeType(name, DateTools.Resolution.MILLISECOND)},
                {FieldDefinitionTypes.DateYear, name => new DateTimeType(name, DateTools.Resolution.YEAR)},
                {FieldDefinitionTypes.DateMonth, name => new DateTimeType(name, DateTools.Resolution.MONTH)},
                {FieldDefinitionTypes.DateDay, name => new DateTimeType(name, DateTools.Resolution.DAY)},
                {FieldDefinitionTypes.DateHour, name => new DateTimeType(name, DateTools.Resolution.HOUR)},
                {FieldDefinitionTypes.DateMinute, name => new DateTimeType(name, DateTools.Resolution.MINUTE)},
                {FieldDefinitionTypes.Raw, name => new RawStringType(name)},
                {FieldDefinitionTypes.FullText, name => new FullTextType(name)},
                {FieldDefinitionTypes.FullTextSortable, name => new FullTextType(name, true)},
                {FieldDefinitionTypes.InvariantCultureIgnoreCase, name => new GenericAnalyzerValueType(name, new CultureInvariantWhitespaceAnalyzer())},
                {FieldDefinitionTypes.EmailAddress, name => new GenericAnalyzerValueType(name, new EmailAddressAnalyzer())}
            };
        
        /// <summary>
        /// this flag indicates if Examine should wait for the current index queue to be fully processed during appdomain shutdown
        /// </summary>
        /// <remarks>
        /// By default this is true but in some cases a user may wish to disable this since this can block an appdomain from shutting down
        /// within a reasonable time which can cause problems with overlapping appdomains.
        /// </remarks>
        public bool WaitForIndexQueueOnShutdown { get; set; }

        /// <summary>
        /// The default analyzer to use when indexing content, by default, this is set to StandardAnalyzer
        /// </summary>
        public Analyzer DefaultAnalyzer
        {
            get; private set;
        }

        private PerFieldAnalyzerWrapper _fieldAnalyzer;
        public PerFieldAnalyzerWrapper FieldAnalyzer => _fieldAnalyzer
            ?? (_fieldAnalyzer =
                (DefaultAnalyzer is PerFieldAnalyzerWrapper pfa)
                    ? pfa
                    : new PerFieldAnalyzerWrapper(DefaultAnalyzer));

        /// <summary>
        /// Used to keep track of how many index commits have been performed.
        /// This is used to determine when index optimization needs to occur.
        /// </summary>
        public int CommitCount { get; protected internal set; }

        /// <summary>
        /// Indicates whether or this system will process the queue items asynchonously - used for testing
        /// </summary>
        internal bool RunAsync { get; set; } = true;

        /// <summary>
        /// The folder that stores the Lucene Index files
        /// </summary>
        public DirectoryInfo LuceneIndexFolder { get; protected set; }
        
        /// <summary>
        /// returns true if the indexer has been canceled (app is shutting down)
        /// </summary>
        protected bool IsCancellationRequested => _cancellationTokenSource.IsCancellationRequested;

        #endregion

        #region Events
        
        /// <summary>
        /// Occurs when [document writing].
        /// </summary>
        public event EventHandler<DocumentWritingEventArgs> DocumentWriting;

        #endregion

        #region Event handlers

        /// <summary>
        /// Called when an indexing error occurs
        /// </summary>
        /// <param name="e"></param>
        /// <param name="resetIndexingFlag">set to true if the IsIndexing flag should be reset (set to false) so future indexing operations can occur</param>
        protected void OnIndexingError(IndexingErrorEventArgs e, bool resetIndexingFlag)
        {
            if (resetIndexingFlag)
            {
                //reset our volatile flag... something else funny is going on but we don't want this to prevent ALL future operations
                _isIndexing = false;
            }

            OnIndexingError(e);
        }

        /// <summary>
        /// Called when an indexing error occurs
        /// </summary>
        /// <param name="e"></param>
        protected override void OnIndexingError(IndexingErrorEventArgs e)
        {
            base.OnIndexingError(e);

#if FULLDEBUG
            Trace.TraceError("Indexing Error Occurred: " + (e.InnerException == null ? e.Message : e.Message + " -- " + e.InnerException));
#endif

            if (!RunAsync)
            {
                var msg = "Indexing Error Occurred: " + e.Message;
                if (e.InnerException != null)
                    msg += ". ERROR: " + e.InnerException.Message;
                throw new Exception(msg, e.InnerException);
            }

        }

        protected virtual void OnDocumentWriting(DocumentWritingEventArgs docArgs)
        {
            DocumentWriting?.Invoke(this, docArgs);
        }

        #endregion

        #region Provider implementation

        protected override void PerformIndexItems(IEnumerable<ValueSet> values, Action<IndexOperationEventArgs> onComplete)
        {
            //need to lock, we don't want to issue any node writing if there's an index rebuild occuring
            Monitor.Enter(_writerLocker);
            try
            {
                Interlocked.Increment(ref _activeAddsOrDeletes);

                try
                {
                    //enqueue the batch, this allows lazy enumeration of the items
                    // when the indexes starts to process
                    QueueIndexOperation(
                        values.Select(value => new IndexOperation(value, IndexOperationType.Add)));

                    //run the indexer on all queued files
                    SafelyProcessQueueItems(onComplete);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeAddsOrDeletes);
                }
            }
            finally
            {
                Monitor.Exit(_writerLocker);
            }
        }
        
        /// <summary>
        /// Creates a brand new index, this will override any existing index with an empty one
        /// </summary>
        public void EnsureIndex(bool forceOverwrite)
        {
            if (!forceOverwrite && _exists.HasValue && _exists.Value) return;

            var indexExists = IndexExists();
            if (!indexExists || forceOverwrite)
            {
                //if we can't acquire the lock exit - this will happen if this method is called multiple times but we don't want this 
                // logic to actually execute multiple times
                if (Monitor.TryEnter(_writerLocker))
                {
                    try
                    {
                        var dir = GetLuceneDirectory();

                        if (!indexExists)
                        {
                            //if there's no index, we need to create one
                            CreateNewIndex(dir);
                        }
                        else
                        {
                            //it does exists so we'll need to clear it out

                            Trace.WriteLine("Initializing new index");

                            if (_writer == null)
                            {
                                //This will happen if the writer hasn't been created/initialized yet which
                                // might occur if a rebuild is triggered before any indexing has been triggered.
                                //In this case we need to initialize a writer and continue as normal.
                                //Since we are already inside the writer lock and it is null, we are allowed to 
                                // make this call with out using GetIndexWriter() to do the initialization.
                                _writer = CreateIndexWriter();
                            }

                            //We're forcing an overwrite, 
                            // this means that we need to cancel all operations currently in place,
                            // clear the queue and delete all of the data in the index.

                            //cancel any operation currently in place
                            _cancellationTokenSource.Cancel();

                            try
                            {
                                //clear the queue
                                IEnumerable<IndexOperation> op;
                                while (_indexQueue.TryTake(out op))
                                {
                                }

                                //remove all of the index data
                                _writer.DeleteAll();
                                _writer.Commit();

                                //we're rebuilding so all old readers referencing this dir should be closed
                                OpenReaderTracker.Current.CloseStaleReaders(dir, TimeSpan.FromMinutes(1));
                            }
                            finally
                            {
                                _cancellationTokenSource = new CancellationTokenSource();
                            }
                        }
                    }
                    finally
                    {
                        Monitor.Exit(_writerLocker);
                    }
                }
                else
                {
                    // we cannot acquire the lock, this is because the main writer is being created, or the index is being created currently
                    OnIndexingError(new IndexingErrorEventArgs(this, "Could not acquire lock in EnsureIndex so cannot create new index", null, null));
                }
            }
        }

        /// <summary>
        /// Used internally to create a brand new index, this is not thread safe
        /// </summary>
        private void CreateNewIndex(Directory dir)
        {
            IndexWriter writer = null;
            try
            {
                if (IndexWriter.IsLocked(dir))
                {
                    //unlock it!
                    IndexWriter.Unlock(dir);
                }
                //create the writer (this will overwrite old index files)
                writer = new IndexWriter(dir, FieldAnalyzer, true, IndexWriter.MaxFieldLength.UNLIMITED);
            }
            catch (Exception ex)
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "An error occurred creating the index", null, ex));
                return;
            }
            finally
            {
                if (writer != null)
                {
                    writer.Dispose();
                }
                _exists = true;
            }
        }


        /// <summary>
        /// Creates a new index, any existing index will be deleted
        /// </summary>
        public override void CreateIndex()
        {
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "Cannot create a new index, indexing cancellation has been requested", null, null));
                return;
            }
            EnsureIndex(true);
        }

        /// <summary>
        /// Deletes a node from the index.                
        /// </summary>
        /// <remarks>
        /// When a content node is deleted, we also need to delete it's children from the index so we need to perform a 
        /// custom Lucene search to find all decendents and create Delete item queues for them too.
        /// </remarks>
        /// <param name="itemId">ID of the node to delete</param>
        /// <param name="onComplete"></param>
        protected override void PerformDeleteFromIndex(string itemId, Action<IndexOperationEventArgs> onComplete)
        {
            Interlocked.Increment(ref _activeAddsOrDeletes);

            try
            {
                QueueIndexOperation(new IndexOperation(new ValueSet(itemId), IndexOperationType.Delete));
                SafelyProcessQueueItems(onComplete);
            }
            finally
            {
                Interlocked.Decrement(ref _activeAddsOrDeletes);
            }
        }

        #endregion

        /// <summary>
        /// This wil optimize the index for searching, this gets executed when this class instance is instantiated.
        /// </summary>
        /// <remarks>
        /// This can be an expensive operation and should only be called when there is no indexing activity
        /// </remarks>
        public void OptimizeIndex()
        {
            if (_cancellationTokenSource.IsCancellationRequested)
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "Cannot optimize index, index cancellation has been requested", null, null), true);
                return;
            }

            try
            {
                if (!IndexExists())
                    return;

                //check if the index is ready to be written to.
                if (!IndexReady())
                {
                    OnIndexingError(new IndexingErrorEventArgs(this, "Cannot optimize index, the index is currently locked", null, null), true);
                    return;
                }

                //open the writer for optization
                var writer = GetIndexWriter();

                //wait for optimization to complete (true)
                writer.Optimize(true);
            }
            catch (Exception ex)
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "Error optimizing Lucene index", null, ex));
            }

        }

        #region Protected

        

        /// <summary>
        /// Creates the <see cref="FieldValueTypeCollection"/> for this index
        /// </summary>
        /// <param name="x"></param>
        /// <param name="indexValueTypesFactory"></param>
        /// <returns></returns>
        protected virtual FieldValueTypeCollection CreateFieldValueTypes(IReadOnlyDictionary<string, Func<string, IIndexValueType>> indexValueTypesFactory = null)
        {
            //copy to writable dictionary
            var defaults = new Dictionary<string, Func<string, IIndexValueType>>();
            foreach (var defaultIndexValueType in DefaultIndexValueTypes)
            {
                defaults[defaultIndexValueType.Key] = defaultIndexValueType.Value;
            }
            //copy the factory over the defaults
            if (indexValueTypesFactory != null)
            {
                foreach (var value in indexValueTypesFactory)
                {
                    defaults[value.Key] = value.Value;
                }
            }

            var result = new FieldValueTypeCollection(FieldAnalyzer, defaults, FieldDefinitionCollection);
            return result;
        }

        /// <summary>
        /// Checks if the index is ready to open/write to.
        /// </summary>
        /// <returns></returns>
        protected bool IndexReady()
        {
            return _writer != null || (!IndexWriter.IsLocked(GetLuceneDirectory()));
        }

        /// <summary>
        /// Check if there is an index in the index folder
        /// </summary>
        /// <returns></returns>

        public override bool IndexExists()
        {
            return _writer != null || IndexExistsImpl();
        }

        /// <summary>
        /// Check if the index is readable/healthy
        /// </summary>
        /// <returns></returns>
        public bool IsReadable(out Exception ex)
        {
            if (_writer != null)
            {
                try
                {
                    using (_writer.GetReader())
                    {
                        ex = null;
                        return true;
                    }
                }
                catch (Exception e)
                {
                    ex = e;
                    return false;
                }
            }

            try
            {
                using (IndexReader.Open(GetLuceneDirectory(), true))
                {
                }
                ex = null;
                return true;
            }
            catch (Exception e)
            {
                ex = e;
                return false;
            }
        }

        /// <summary>
        /// This will check one time if the index exists, we don't want to keep using IndexReader.IndexExists because that will literally go list
        /// every file in the index folder and we don't need any more IO ops
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// If the index does not exist, it will not store the value so subsequent calls to this will re-evaulate
        /// </remarks>

        private bool IndexExistsImpl()
        {
            //if it's been set and it's true, return true
            if (_exists.HasValue && _exists.Value) return true;

            //if it's not been set or it just doesn't exist, re-read the lucene files
            if (!_exists.HasValue || !_exists.Value)
            {
                _exists = IndexReader.IndexExists(GetLuceneDirectory());
            }

            return _exists.Value;
        }



        /// <summary>
        /// Removes the specified term from the index
        /// </summary>
        /// <param name="indexTerm"></param>
        /// <param name="iw"></param>
        /// <param name="performCommit"></param>
        /// <returns>Boolean if it successfully deleted the term, or there were on errors</returns>
        protected bool DeleteFromIndex(Term indexTerm, IndexWriter iw, bool performCommit = true)
        {
            string itemId = null;
            if (indexTerm.Field == "id")
            {
                itemId = indexTerm.Text;
            }

            try
            {
                //if the index doesn't exist, then no don't attempt to open it.
                if (!IndexExists())
                    return true;

                iw.DeleteDocuments(indexTerm);

                if (performCommit)
                {
                    iw.Commit();
                }

                return true;
            }
            catch (Exception ee)
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "Error deleting Lucene index", itemId, ee));
                return false;
            }
        }

        /// <summary>
        /// Collects the data for the fields and adds the document which is then committed into Lucene.Net's index
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="valueSet">The data to index.</param>
        /// <param name="writer">The writer that will be used to update the Lucene index.</param>
        protected virtual void AddDocument(Document doc, ValueSet valueSet, IndexWriter writer)
        {
            //add node id
            var nodeIdValueType = FieldValueTypeCollection.GetValueType(ItemIdFieldName, FieldValueTypeCollection.ValueTypeFactories[FieldDefinitionTypes.Raw]);
            nodeIdValueType.AddValue(doc, valueSet.Id);
            //add the category
            var categoryValueType = FieldValueTypeCollection.GetValueType(CategoryFieldName, FieldValueTypeCollection.ValueTypeFactories[FieldDefinitionTypes.InvariantCultureIgnoreCase]);
            categoryValueType.AddValue(doc, valueSet.Category);
            //add the item type
            var indexTypeValueType = FieldValueTypeCollection.GetValueType(ItemTypeFieldName, FieldValueTypeCollection.ValueTypeFactories[FieldDefinitionTypes.InvariantCultureIgnoreCase]);
            indexTypeValueType.AddValue(doc, valueSet.ItemType);

            foreach (var field in valueSet.Values)
            {
                //check if we have a defined one
                if (FieldDefinitionCollection.TryGetValue(field.Key, out var definedFieldDefinition))
                {
                    var valueType = FieldValueTypeCollection.GetValueType(
                        definedFieldDefinition.Name,
                        FieldValueTypeCollection.ValueTypeFactories.TryGetValue(definedFieldDefinition.Type, out var valTypeFactory)
                            ? valTypeFactory
                            : FieldValueTypeCollection.ValueTypeFactories[FieldDefinitionTypes.FullText]);

                    foreach (var o in field.Value)
                    {
                        valueType.AddValue(doc, o);
                    }
                }
                else if (field.Key.StartsWith(SpecialFieldPrefix))
                {
                    //Check for the special field prefix, if this is the case it's indexed as an invariant culture value

                    var valueType = FieldValueTypeCollection.GetValueType(field.Key, FieldValueTypeCollection.ValueTypeFactories[FieldDefinitionTypes.InvariantCultureIgnoreCase]);
                    foreach (var o in field.Value)
                    {
                        valueType.AddValue(doc, o);
                    }
                }
                else
                {
                    //try to find the field definition for this field, if nothing is found use the default
                    var def = FieldDefinitionCollection.GetOrAdd(field.Key, s => new FieldDefinition(s, FieldDefinitionTypes.FullText));

                    var valueType = FieldValueTypeCollection.GetValueType(def.Name, FieldValueTypeCollection.ValueTypeFactories[FieldDefinitionTypes.FullText]);
                    foreach (var o in field.Value)
                    {
                        valueType.AddValue(doc, o);
                    }
                }
            }

            var docArgs = new DocumentWritingEventArgs(valueSet, doc);
            OnDocumentWriting(docArgs);
            if (docArgs.Cancel)
                return;

            writer.UpdateDocument(new Term(ItemIdFieldName, valueSet.Id), doc);
        }


        /// <summary>
        /// Process all of the queue items
        /// </summary>
        /// <param name="onComplete"></param>
        private void SafelyProcessQueueItems(Action<IndexOperationEventArgs> onComplete)
        {
            if (!RunAsync)
            {
                StartIndexing(onComplete);
            }
            else
            {
                if (!_isIndexing)
                {
                    //don't run the worker if it's currently running since it will just pick up the rest of the queue during its normal operation                    
                    lock (_indexingLocker)
                    {
                        if (!_isIndexing && (_asyncTask == null || _asyncTask.IsCompleted))
                        {
                            //Trace.WriteLine("Examine: Launching task");
                            if (!_cancellationTokenSource.IsCancellationRequested)
                            {
                                _asyncTask = Task.Factory.StartNew(
                                    () =>
                                    {
                                        //Ensure the indexing processes is using an invariant culture
                                        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                                        StartIndexing(onComplete);
                                    },
                                    _cancellationTokenSource.Token,  //use our cancellation token
                                    TaskCreationOptions.None,
                                    TaskScheduler.Default).ContinueWith(task =>
                                    {
                                        if (task.IsCanceled)
                                        {
                                            //if this gets cancelled, we need to ... ?
                                        }
                                    });
                            }
                        }
                    }
                }
            }

        }

        /// <summary>
        /// Processes the queue and checks if optimization needs to occur at the end
        /// </summary>
        /// <param name="onComplete"></param>
        void StartIndexing(Action<IndexOperationEventArgs> onComplete)
        {
            if (!_isIndexing)
            {
                lock (_indexingLocker)
                {
                    if (!_isIndexing && !_cancellationTokenSource.IsCancellationRequested)
                    {

                        _isIndexing = true;

                        //keep processing until it is complete
                        var numProcessedItems = 0;
                        do
                        {
                            numProcessedItems = ForceProcessQueueItems();
                        } while (numProcessedItems > 0);

                        //reset the flag
                        _isIndexing = false;

                        onComplete?.Invoke(new IndexOperationEventArgs(this, numProcessedItems));
                    }
                }
            }

        }

        /// <summary>
        /// Loop through all files in the queue item folder and index them.
        /// Regardless of weather this machine is the executive indexer or not or is in a load balanced environment
        /// or not, this WILL attempt to process the queue items into the index.
        /// </summary>
        /// <returns>
        /// The number of queue items processed
        /// </returns>
        /// <remarks>
        /// Inheritors should be very carefully using this method, SafelyProcessQueueItems will ensure
        /// that the correct machine processes the items into the index. SafelyQueueItems calls this method
        /// if it confirms that this machine is the one to process the queue.
        /// </remarks>

        protected int ForceProcessQueueItems()
        {
            return ForceProcessQueueItems(false);
        }

        /// <summary>
        /// Loop through all files in the queue item folder and index them.
        /// Regardless of weather this machine is the executive indexer or not or is in a load balanced environment
        /// or not, this WILL attempt to process the queue items into the index.
        /// </summary>
        /// <returns>
        /// The number of queue items processed
        /// </returns>
        /// <remarks>
        /// The 'block' parameter is very important, normally this will not block since we're running on a background thread anyways, however
        /// during app shutdown we want to process the remaining queue and block.
        /// </remarks>
        private int ForceProcessQueueItems(bool block)
        {
            //check if the index is ready to be written to.
            if (!IndexReady())
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "Cannot index queue items, the index is currently locked", null, null));
                return 0;
            }

            //track all of the nodes indexed
            var indexedNodes = 0;

            Interlocked.Increment(ref _activeWrites);

            try
            {
                var writer = GetIndexWriter();

                if (block)
                {
                    if (!_indexQueue.IsAddingCompleted)
                    {
                        throw new InvalidOperationException("Cannot block unless the queue is finalized");
                    }

                    foreach (var batch in _indexQueue.GetConsumingEnumerable())
                        foreach (var item in batch)
                            if (ProcessQueueItem(item, writer))
                                indexedNodes++;
                }
                else
                {
                    //index while we're not cancelled and while there's items in there
                    while (!_cancellationTokenSource.IsCancellationRequested && _indexQueue.TryTake(out var batch))
                        foreach (var item in batch)
                            if (ProcessQueueItem(item, writer))
                                indexedNodes++;
                }

                //this is required to ensure the index is written to during the same thread execution
                // if we are in blocking mode, the do the wait
                if (!RunAsync || block)
                {
                    //commit the changes (this will process the deletes too)
                    writer.Commit();

                    writer.WaitForMerges();
                }
                else
                {
                    _committer.ScheduleCommit();
                }
            }
            catch (Exception ex)
            {
                OnIndexingError(new IndexingErrorEventArgs(this, "Error indexing queue items", null, ex));
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }

            return indexedNodes;
        }

        /// <summary>
        /// This queues up a commit for the index so that a commit doesn't happen on every individual write since that is quite expensive
        /// </summary>
        private class IndexCommiter : DisposableObjectSlim
        {
            private readonly LuceneIndex _index;
            private DateTime _timestamp;
            private Timer _timer;
            private readonly object _locker = new object();
            private const int WaitMilliseconds = 2000;

            /// <summary>
            /// The maximum time period that will elapse until we must commit (5 mins)
            /// </summary>
            private const int MaxWaitMilliseconds = 300000;

            public IndexCommiter(LuceneIndex index)
            {
                _index = index;
            }


            public void ScheduleCommit()
            {
                lock (_locker)
                {
                    if (_timer == null)
                    {
                        //if we've been cancelled then be sure to commit now
                        if (_index._cancellationTokenSource.IsCancellationRequested)
                        {
                            //perform the commit
                            _index._writer?.Commit();
                        }
                        else
                        {
                            //It's the initial call to this at the beginning or after successful commit
                            _timestamp = DateTime.Now;
                            _timer = new Timer(_ => TimerRelease());
                            _timer.Change(WaitMilliseconds, 0);
                        }
                    }
                    else
                    {
                        //if we've been cancelled then be sure to cancel the timer and commit now
                        if (_index._cancellationTokenSource.IsCancellationRequested)
                        {
                            //Stop the timer
                            _timer.Change(Timeout.Infinite, Timeout.Infinite);
                            _timer.Dispose();
                            _timer = null;

                            //perform the commit
                            _index._writer?.Commit();
                        }
                        else if (
                            // must be less than the max
                            DateTime.Now - _timestamp < TimeSpan.FromMilliseconds(MaxWaitMilliseconds) &&
                            // and less than the delay
                            DateTime.Now - _timestamp < TimeSpan.FromMilliseconds(WaitMilliseconds))
                        {
                            //Delay  
                            _timer.Change(WaitMilliseconds, 0);
                        }
                        else
                        {
                            //Cannot delay! the callback will execute on the pending timeout
                        }
                    }
                }
            }


            private void TimerRelease()
            {
                lock (_locker)
                {
                    //if the timer is not null then a commit has been scheduled
                    if (_timer != null)
                    {
                        //Stop the timer
                        _timer.Change(Timeout.Infinite, Timeout.Infinite);
                        _timer.Dispose();
                        _timer = null;

                        //perform the commit
                        _index._writer?.Commit();
                    }
                }
            }

            protected override void DisposeResources()
            {
                TimerRelease();
            }
        }


        private bool ProcessQueueItem(IndexOperation item, IndexWriter writer)
        {
            switch (item.Operation)
            {
                case IndexOperationType.Add:

                    var added = ProcessIndexQueueItem(item, writer);
                    return added;
                case IndexOperationType.Delete:
                    ProcessDeleteQueueItem(item, writer, false);
                    return true;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Queues an indexing operation
        /// </summary>
        /// <param name="op"></param>
        protected void QueueIndexOperation(IndexOperation op)
        {
            //don't queue if there's been a cancellation requested
            if (!_cancellationTokenSource.IsCancellationRequested && !_indexQueue.IsAddingCompleted)
            {
                _indexQueue.Add(new[] { op });
            }
            else
            {
                OnIndexingError(
                    new IndexingErrorEventArgs(this,
                        "App is shutting down so index operation is ignored: " + op.ValueSet.Id, null, null));
            }
        }

        /// <summary>
        /// Queues an indexing operation batch
        /// </summary>
        /// <param name="ops"></param>
        protected void QueueIndexOperation(IEnumerable<IndexOperation> ops)
        {
            //don't queue if there's been a cancellation requested
            if (!_cancellationTokenSource.IsCancellationRequested && !_indexQueue.IsAddingCompleted)
            {
                _indexQueue.Add(ops);
            }
            else
            {
                OnIndexingError(
                    new IndexingErrorEventArgs(this,
                        "App is shutting down so index batch operation is ignored", null, null));
            }
        }

       

        /// <summary>
        /// Initialize the directory
        /// </summary>
        private Directory InitializeDirectory()
        {
            if (_directory != null) return _directory;


            //ensure all of the folders are created at startup
            if (!VerifyFolder(LuceneIndexFolder))
                throw new InvalidOperationException("The indexFolder was not specified");

            if (DirectoryFactory == null)
            {   
                var simpleFsDirectory = new SimpleFSDirectory(LuceneIndexFolder);
                simpleFsDirectory.SetLockFactory(Directories.DirectoryFactory.DefaultLockFactory(LuceneIndexFolder));
                return simpleFsDirectory;
            }

            return DirectoryFactory.CreateDirectory(this, LuceneIndexFolder.FullName);
        }

        private Directory _directory;

        /// <summary>
        /// Gets the <see cref="IDirectoryFactory"/> if one is being used
        /// </summary>
        public IDirectoryFactory DirectoryFactory { get; private set; }

        /// <summary>
        /// Returns the Lucene Directory used to store the index
        /// </summary>
        /// <returns></returns>

        public virtual Directory GetLuceneDirectory()
        {
            return _writer != null ? _writer.Directory : _directory;
        }

        private FileStream _logOutput;

        /// <summary>
        /// Used to create an index writer - this is called in GetIndexWriter (and therefore, GetIndexWriter should not be overridden)
        /// </summary>
        /// <returns></returns>

        protected virtual IndexWriter CreateIndexWriter()
        {
            var writer = WriterTracker.Current.GetWriter(
                GetLuceneDirectory(),
                WriterFactory);

#if FULLDEBUG
            //If we want to enable logging of lucene output....
            //It is also possible to set a default InfoStream on the static IndexWriter class            
            _logOutput?.Close();
            if (LuceneIndexFolder != null)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(LuceneIndexFolder.FullName);
                    _logOutput = new FileStream(Path.Combine(LuceneIndexFolder.FullName, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".log"), FileMode.Append);
                    var w = new StreamWriter(_logOutput);
                    writer.SetInfoStream(w);
                }
                catch (Exception ex)
                {
                    //if an exception is thrown here we won't worry about it, it will mean we cannot create the log file
                }
            }

#endif

            return writer;
        }

        /// <summary>
        /// Purely to do with stupid medium trust
        /// </summary>
        /// <param name="d"></param>
        /// <returns></returns>
        private IndexWriter WriterFactory(Directory d)
        {
            if (d == null) throw new ArgumentNullException(nameof(d));
            var writer = new IndexWriter(d, FieldAnalyzer, false, IndexWriter.MaxFieldLength.UNLIMITED);
            return writer;
        }

        /// <summary>
        /// Returns an index writer for the current directory
        /// </summary>
        /// <returns></returns>
        public IndexWriter GetIndexWriter()
        {
            EnsureIndex(false);

            if (_writer == null)
            {
                Monitor.Enter(_writerLocker);
                try
                {
                    if (_writer == null)
                    {
                        _writer = CreateIndexWriter();
                    }
                }
                finally
                {
                    Monitor.Exit(_writerLocker);
                }

            }

            return _writer;
        }

        #endregion

        #region Private

        private LuceneSearcher CreateSearcher()
        {
            //trim the "Indexer" / "Index" suffix if it exists
            var name = Name.EndsWith("Indexer") ? Name.Substring(0, Name.LastIndexOf("Indexer", StringComparison.Ordinal)) : Name;
            name = name.EndsWith("Index") ? Name.Substring(0, Name.LastIndexOf("Index", StringComparison.Ordinal)) : Name;
            return new LuceneSearcher(name + "Searcher", GetIndexWriter(), FieldAnalyzer, FieldValueTypeCollection);
        }


        /// <summary>
        /// Deletes the item from the index either by id or by category
        /// </summary>
        /// <param name="op"></param>
        /// <param name="iw"></param>
        /// <param name="performCommit"></param>
        private void ProcessDeleteQueueItem(IndexOperation op, IndexWriter iw, bool performCommit = true)
        {

            //if the id is empty then remove the whole type
            if (!string.IsNullOrEmpty(op.ValueSet.Id))
            {
                DeleteFromIndex(new Term(ItemIdFieldName, op.ValueSet.Id), iw, performCommit);
            }
            else if (!string.IsNullOrEmpty(op.ValueSet.Category))
            {
                DeleteFromIndex(new Term(CategoryFieldName, op.ValueSet.Category), iw, performCommit);
            }

            CommitCount++;
        }


        private bool ProcessIndexQueueItem(IndexOperation op, IndexWriter writer)
        {
            
            //raise the event and assign the value to the returned data from the event
            var indexingNodeDataArgs = new IndexingItemEventArgs(this, op.ValueSet);
            OnTransformingIndexValues(indexingNodeDataArgs);
            if (indexingNodeDataArgs.Cancel) return false;

            var d = new Document();
            AddDocument(d, op.ValueSet, writer);

            CommitCount++;

            return true;
        }

        /// <summary>
        /// Creates the folder if it does not exist.
        /// </summary>
        /// <param name="folder"></param>
        private bool VerifyFolder(DirectoryInfo folder)
        {
            if (folder == null)
                return false;

            if (!System.IO.Directory.Exists(folder.FullName))
            {
                lock (_folderLocker)
                {
                    if (!System.IO.Directory.Exists(folder.FullName))
                    {
                        System.IO.Directory.CreateDirectory(folder.FullName);
                        folder.Refresh();
                    }
                }
            }

            return true;
        }

        #endregion

        /// <summary>
        /// Used to force the index into synchronous index processing
        /// </summary>
        /// <returns></returns>
        public IDisposable ProcessNonAsync()
        {
            return new SynchronousIndexProcessor(this);
        }

        /// <summary>
        /// Used to force the index into synchronous index processing
        /// </summary>
        private class SynchronousIndexProcessor : DisposableObjectSlim
        {
            private readonly LuceneIndex _index;

            public SynchronousIndexProcessor(LuceneIndex index)
            {
                _index = index;
                _index.RunAsync = false;
            }

            protected override void DisposeResources()
            {
                _index.RunAsync = true;
            }
        }

        #region IDisposable Members

        private readonly DisposableIndex _disposer;
        private readonly IndexCommiter _committer;

        private class DisposableIndex : DisposableObjectSlim
        {
            private readonly LuceneIndex _index;

            public DisposableIndex(LuceneIndex index)
            {
                _index = index;
            }

            /// <summary>
            /// Handles the disposal of resources. Derived from abstract class <see cref="DisposableObject"/> which handles common required locking logic.
            /// </summary>

            protected override void DisposeResources()
            {

                if (_index.WaitForIndexQueueOnShutdown)
                {
                    //if there are active adds, lets way/retry (5 seconds)
                    RetryUntilSuccessOrTimeout(() => _index._activeAddsOrDeletes == 0, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
                }

                //cancel any operation currently in place
                _index._cancellationTokenSource.Cancel();

                //ensure nothing more can be added
                _index._indexQueue.CompleteAdding();

                if (_index._writer != null)
                {
                    if (_index.WaitForIndexQueueOnShutdown)
                    {
                        //process remaining items and block until complete
                        _index.ForceProcessQueueItems(true);
                    }
                }

                //dispose it now
                _index._indexQueue.Dispose();

                //Don't close the writer until there are definitely no more writes
                //NOTE: we are not taking into acccount the WaitForIndexQueueOnShutdown property here because we really want to make sure
                //we are not terminating Lucene while it is actively writing to the index.
                RetryUntilSuccessOrTimeout(() => _index._activeWrites == 0, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

                //close the committer, this will ensure a final commit is made if one has been queued
                _index._committer.Dispose();

                _index._writer?.Dispose();

                _index._cancellationTokenSource.Dispose();

                _index._logOutput?.Close();
            }

            private static bool RetryUntilSuccessOrTimeout(Func<bool> task, TimeSpan timeout, TimeSpan pause)
            {

                if (pause.TotalMilliseconds < 0)
                {
                    throw new ArgumentException("pause must be >= 0 milliseconds");
                }
                var stopwatch = Stopwatch.StartNew();
                do
                {
                    if (task()) { return true; }
                    Thread.Sleep((int)pause.TotalMilliseconds);
                }
                while (stopwatch.Elapsed < timeout);
                return false;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (_searcher.IsValueCreated)
            {
                _searcher.Value.Dispose();
            }
            _disposer.Dispose();
        }

        #endregion


    }

    
}

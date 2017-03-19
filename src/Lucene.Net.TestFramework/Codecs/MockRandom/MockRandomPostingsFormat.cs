﻿using Lucene.Net.Codecs.BlockTerms;
using Lucene.Net.Codecs.Lucene41;
using Lucene.Net.Codecs.Memory;
using Lucene.Net.Codecs.MockIntBlock;
using Lucene.Net.Codecs.MockSep;
using Lucene.Net.Codecs.Pulsing;
using Lucene.Net.Codecs.Sep;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Lucene.Net.Codecs.MockRandom
{
    /// <summary>
    /// Randomly combines terms index impl w/ postings impls.
    /// </summary>
    [PostingsFormatName("MockRandom")]
    public sealed class MockRandomPostingsFormat : PostingsFormat
    {
        private readonly Random seedRandom;
        private readonly string SEED_EXT = "sd";

        private class RandomAnonymousClassHelper : Random
        {
            public RandomAnonymousClassHelper()
                : base(0)
            {
            }

            public override int Next(int maxValue)
            {
                throw new InvalidOperationException("Please use MockRandomPostingsFormat(Random)");
            }
        }

        public MockRandomPostingsFormat()
                  : this(null)
        {
            // This ctor should *only* be used at read-time: get NPE if you use it!
        }

        public MockRandomPostingsFormat(Random random)
            : base()
        {
            if (random == null)
            {
                this.seedRandom = new RandomAnonymousClassHelper();
                //            this.seedRandom = new Random(0) {

                //    protected override int Next(int arg0)
                //    {
                //        throw new IllegalStateException("Please use MockRandomPostingsFormat(Random)");
                //    }
                //};
            }
            else
            {
                this.seedRandom = new Random(random.Next());
            }
        }

        // Chooses random IntStreamFactory depending on file's extension
        private class MockInt32StreamFactory : Int32StreamFactory
        {
            private readonly int salt;
            private readonly IList<Int32StreamFactory> delegates = new List<Int32StreamFactory>();

            public MockInt32StreamFactory(Random random)
            {
                salt = random.nextInt();
                delegates.Add(new MockSingleIntFactory());
                int blockSize = TestUtil.NextInt(random, 1, 2000);
                delegates.Add(new MockFixedIntBlockPostingsFormat.MockIntFactory(blockSize));
                int baseBlockSize = TestUtil.NextInt(random, 1, 127);
                delegates.Add(new MockVariableInt32BlockPostingsFormat.MockInt32Factory(baseBlockSize));
                // TODO: others
            }

            private static String getExtension(String fileName)
            {
                int idx = fileName.IndexOf('.');
                Debug.Assert(idx != -1);
                return fileName.Substring(idx);
            }


            public override Int32IndexInput OpenInput(Directory dir, string fileName, IOContext context)
            {
                // Must only use extension, because IW.addIndexes can
                // rename segment!
                Int32StreamFactory f = delegates[(Math.Abs(salt ^ getExtension(fileName).GetHashCode())) % delegates.size()];
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: read using int factory " + f + " from fileName=" + fileName);
                }
                return f.OpenInput(dir, fileName, context);
            }

            public override Int32IndexOutput CreateOutput(Directory dir, string fileName, IOContext context)
            {
                Int32StreamFactory f = delegates[(Math.Abs(salt ^ getExtension(fileName).GetHashCode())) % delegates.size()];
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: write using int factory " + f + " to fileName=" + fileName);
                }
                return f.CreateOutput(dir, fileName, context);
            }
        }

        private class IndexTermSelectorAnonymousHelper : VariableGapTermsIndexWriter.IndexTermSelector
        {
            private readonly Random rand;
            private readonly int gap;
            public IndexTermSelectorAnonymousHelper(int seed, int gap)
            {
                rand = new Random(seed);
                this.gap = gap;
            }
            public override bool IsIndexTerm(BytesRef term, TermStats stats)
            {
                return rand.Next(gap) == gap / 2;
            }

            public override void NewField(FieldInfo fieldInfo)
            {
            }
        }

        public override FieldsConsumer FieldsConsumer(SegmentWriteState state)
        {
            int minSkipInterval;
            if (state.SegmentInfo.DocCount > 1000000)
            {
                // Test2BPostings can OOME otherwise:
                minSkipInterval = 3;
            }
            else
            {
                minSkipInterval = 2;
            }

            // we pull this before the seed intentionally: because its not consumed at runtime
            // (the skipInterval is written into postings header)
            int skipInterval = TestUtil.NextInt(seedRandom, minSkipInterval, 10);

            if (LuceneTestCase.VERBOSE)
            {
                Console.WriteLine("MockRandomCodec: skipInterval=" + skipInterval);
            }

            long seed = seedRandom.nextLong();

            if (LuceneTestCase.VERBOSE)
            {
                Console.WriteLine("MockRandomCodec: writing to seg=" + state.SegmentInfo.Name + " formatID=" + state.SegmentSuffix + " seed=" + seed);
            }

            string seedFileName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix, SEED_EXT);
            IndexOutput @out = state.Directory.CreateOutput(seedFileName, state.Context);
            try
            {
                @out.WriteInt64(seed);
            }
            finally
            {
                @out.Dispose();
            }

            Random random = new Random((int)seed);

            random.nextInt(); // consume a random for buffersize

            PostingsWriterBase postingsWriter;
            if (random.nextBoolean())
            {
                postingsWriter = new SepPostingsWriter(state, new MockInt32StreamFactory(random), skipInterval);
            }
            else
            {
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: writing Standard postings");
                }
                // TODO: randomize variables like acceptibleOverHead?!
                postingsWriter = new Lucene41PostingsWriter(state, skipInterval);
            }

            if (random.nextBoolean())
            {
                int totTFCutoff = TestUtil.NextInt(random, 1, 20);
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: writing pulsing postings with totTFCutoff=" + totTFCutoff);
                }
                postingsWriter = new PulsingPostingsWriter(state, totTFCutoff, postingsWriter);
            }

            FieldsConsumer fields;
            int t1 = random.nextInt(4);

            if (t1 == 0)
            {
                bool success = false;
                try
                {
                    fields = new FSTTermsWriter(state, postingsWriter);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsWriter.Dispose();
                    }
                }
            }
            else if (t1 == 1)
            {
                bool success = false;
                try
                {
                    fields = new FSTOrdTermsWriter(state, postingsWriter);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsWriter.Dispose();
                    }
                }
            }
            else if (t1 == 2)
            {
                // Use BlockTree terms dict

                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: writing BlockTree terms dict");
                }

                // TODO: would be nice to allow 1 but this is very
                // slow to write
                int minTermsInBlock = TestUtil.NextInt(random, 2, 100);
                int maxTermsInBlock = Math.Max(2, (minTermsInBlock - 1) * 2 + random.nextInt(100));

                bool success = false;
                try
                {
                    fields = new BlockTreeTermsWriter(state, postingsWriter, minTermsInBlock, maxTermsInBlock);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsWriter.Dispose();
                    }
                }
            }
            else
            {

                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: writing Block terms dict");
                }

                bool success = false;

                TermsIndexWriterBase indexWriter;
                try
                {
                    if (random.nextBoolean())
                    {
                        state.TermIndexInterval = TestUtil.NextInt(random, 1, 100);
                        if (LuceneTestCase.VERBOSE)
                        {
                            Console.WriteLine("MockRandomCodec: fixed-gap terms index (tii=" + state.TermIndexInterval + ")");
                        }
                        indexWriter = new FixedGapTermsIndexWriter(state);
                    }
                    else
                    {
                        VariableGapTermsIndexWriter.IndexTermSelector selector;
                        int n2 = random.nextInt(3);
                        if (n2 == 0)
                        {
                            int tii = TestUtil.NextInt(random, 1, 100);
                            selector = new VariableGapTermsIndexWriter.EveryNTermSelector(tii);
                            if (LuceneTestCase.VERBOSE)
                            {
                                Console.WriteLine("MockRandomCodec: variable-gap terms index (tii=" + tii + ")");
                            }
                        }
                        else if (n2 == 1)
                        {
                            int docFreqThresh = TestUtil.NextInt(random, 2, 100);
                            int tii = TestUtil.NextInt(random, 1, 100);
                            selector = new VariableGapTermsIndexWriter.EveryNOrDocFreqTermSelector(docFreqThresh, tii);
                        }
                        else
                        {
                            int seed2 = random.Next();
                            int gap = TestUtil.NextInt(random, 2, 40);
                            if (LuceneTestCase.VERBOSE)
                            {
                                Console.WriteLine("MockRandomCodec: random-gap terms index (max gap=" + gap + ")");
                            }
                            selector = new IndexTermSelectorAnonymousHelper(seed2, gap);

                            //           selector = new VariableGapTermsIndexWriter.IndexTermSelector() {
                            //                Random rand = new Random(seed2);

                            //@Override
                            //                public bool isIndexTerm(BytesRef term, TermStats stats)
                            //{
                            //    return rand.nextInt(gap) == gap / 2;
                            //}

                            //@Override
                            //                  public void newField(FieldInfo fieldInfo)
                            //{
                            //}
                            //              };
                        }
                        indexWriter = new VariableGapTermsIndexWriter(state, selector);
                    }
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsWriter.Dispose();
                    }
                }

                success = false;
                try
                {
                    fields = new BlockTermsWriter(indexWriter, state, postingsWriter);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        try
                        {
                            postingsWriter.Dispose();
                        }
                        finally
                        {
                            indexWriter.Dispose();
                        }
                    }
                }
            }

            return fields;
        }

        public override FieldsProducer FieldsProducer(SegmentReadState state)
        {

            string seedFileName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix, SEED_EXT);
            IndexInput @in = state.Directory.OpenInput(seedFileName, state.Context);
            long seed = @in.ReadInt64();
            if (LuceneTestCase.VERBOSE)
            {
                Console.WriteLine("MockRandomCodec: reading from seg=" + state.SegmentInfo.Name + " formatID=" + state.SegmentSuffix + " seed=" + seed);
            }
            @in.Dispose();

            Random random = new Random((int)seed);

            int readBufferSize = TestUtil.NextInt(random, 1, 4096);
            if (LuceneTestCase.VERBOSE)
            {
                Console.WriteLine("MockRandomCodec: readBufferSize=" + readBufferSize);
            }

            PostingsReaderBase postingsReader;

            if (random.nextBoolean())
            {
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: reading Sep postings");
                }
                postingsReader = new SepPostingsReader(state.Directory, state.FieldInfos, state.SegmentInfo,
                                                       state.Context, new MockInt32StreamFactory(random), state.SegmentSuffix);
            }
            else
            {
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: reading Standard postings");
                }
                postingsReader = new Lucene41PostingsReader(state.Directory, state.FieldInfos, state.SegmentInfo, state.Context, state.SegmentSuffix);
            }

            if (random.nextBoolean())
            {
                int totTFCutoff = TestUtil.NextInt(random, 1, 20);
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: reading pulsing postings with totTFCutoff=" + totTFCutoff);
                }
                postingsReader = new PulsingPostingsReader(state, postingsReader);
            }

            FieldsProducer fields;
            int t1 = random.nextInt(4);
            if (t1 == 0)
            {
                bool success = false;
                try
                {
                    fields = new FSTTermsReader(state, postingsReader);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsReader.Dispose();
                    }
                }
            }
            else if (t1 == 1)
            {
                bool success = false;
                try
                {
                    fields = new FSTOrdTermsReader(state, postingsReader);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsReader.Dispose();
                    }
                }
            }
            else if (t1 == 2)
            {
                // Use BlockTree terms dict
                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: reading BlockTree terms dict");
                }

                bool success = false;
                try
                {
                    fields = new BlockTreeTermsReader(state.Directory,
                                                      state.FieldInfos,
                                                      state.SegmentInfo,
                                                      postingsReader,
                                                      state.Context,
                                                      state.SegmentSuffix,
                                                      state.TermsIndexDivisor);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsReader.Dispose();
                    }
                }
            }
            else
            {

                if (LuceneTestCase.VERBOSE)
                {
                    Console.WriteLine("MockRandomCodec: reading Block terms dict");
                }
                TermsIndexReaderBase indexReader;
                bool success = false;
                try
                {
                    bool doFixedGap = random.nextBoolean();

                    // randomness diverges from writer, here:
                    if (state.TermsIndexDivisor != -1)
                    {
                        state.TermsIndexDivisor = TestUtil.NextInt(random, 1, 10);
                    }

                    if (doFixedGap)
                    {
                        // if termsIndexDivisor is set to -1, we should not touch it. It means a
                        // test explicitly instructed not to load the terms index.
                        if (LuceneTestCase.VERBOSE)
                        {
                            Console.WriteLine("MockRandomCodec: fixed-gap terms index (divisor=" + state.TermsIndexDivisor + ")");
                        }
                        indexReader = new FixedGapTermsIndexReader(state.Directory,
                                                                   state.FieldInfos,
                                                                   state.SegmentInfo.Name,
                                                                   state.TermsIndexDivisor,
                                                                   BytesRef.UTF8SortedAsUnicodeComparer,
                                                                   state.SegmentSuffix, state.Context);
                    }
                    else
                    {
                        int n2 = random.nextInt(3);
                        if (n2 == 1)
                        {
                            random.nextInt();
                        }
                        else if (n2 == 2)
                        {
                            random.nextLong();
                        }
                        if (LuceneTestCase.VERBOSE)
                        {
                            Console.WriteLine("MockRandomCodec: variable-gap terms index (divisor=" + state.TermsIndexDivisor + ")");
                        }
                        indexReader = new VariableGapTermsIndexReader(state.Directory,
                                                                      state.FieldInfos,
                                                                      state.SegmentInfo.Name,
                                                                      state.TermsIndexDivisor,
                                                                      state.SegmentSuffix, state.Context);

                    }

                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        postingsReader.Dispose();
                    }
                }

                success = false;
                try
                {
                    fields = new BlockTermsReader(indexReader,
                                                  state.Directory,
                                                  state.FieldInfos,
                                                  state.SegmentInfo,
                                                  postingsReader,
                                                  state.Context,
                                                  state.SegmentSuffix);
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        try
                        {
                            postingsReader.Dispose();
                        }
                        finally
                        {
                            indexReader.Dispose();
                        }
                    }
                }
            }

            return fields;
        }
    }
}

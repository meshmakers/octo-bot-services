// MongoDB Shell TypeScript Definitionen
declare global {
  // MongoDB Shell Globals
  var db: Database;
  var print: (message: any) => void;
  var printjson: (obj: any) => void;
  
  interface Database {
    getName(): string;
    getCollection(name: string): Collection;
    createCollection(name: string, options?: any): Collection;
    dropDatabase(): any;
    runCommand(command: any): any;
    stats(): any;
  }

  interface Collection {
    // CRUD Operations
    find(query?: any, projection?: any): Cursor;
    findOne(query?: any, projection?: any): any;
    insertOne(doc: any): InsertOneResult;
    insertMany(docs: any[]): InsertManyResult;
    updateOne(filter: any, update: any, options?: any): UpdateResult;
    updateMany(filter: any, update: any, options?: any): UpdateResult;
    deleteOne(filter: any): DeleteResult;
    deleteMany(filter: any): DeleteResult;
    
    // Aggregation
    aggregate(pipeline: any[]): AggregationCursor;
    count(query?: any): number;
    countDocuments(filter?: any): number;
    distinct(field: string, query?: any): any[];
    
    // Indexes
    createIndex(keys: any, options?: any): string;
    dropIndex(index: any): any;
    getIndexes(): any[];
    
    // Collection Management  
    drop(): boolean;
    stats(): any;
  }

  interface Cursor {
    toArray(): any[];
    hasNext(): boolean;
    next(): any;
    forEach(func: (doc: any) => void): void;
    map(func: (doc: any) => any): any[];
    limit(num: number): Cursor;
    skip(num: number): Cursor;
    sort(sort: any): Cursor;
    count(): number;
    explain(verbosity?: string): any;
    hint(index: any): Cursor;
  }

  interface AggregationCursor extends Cursor {
    // Aggregation-specific methods
  }

  interface InsertOneResult {
    acknowledged: boolean;
    insertedId: any;
  }

  interface InsertManyResult {
    acknowledged: boolean;
    insertedIds: any[];
  }

  interface UpdateResult {
    acknowledged: boolean;
    matchedCount: number;
    modifiedCount: number;
    upsertedCount: number;
    upsertedId?: any;
  }

  interface DeleteResult {
    acknowledged: boolean;
    deletedCount: number;
  }
}

export {};

import { GraphQLResolveInfo, GraphQLScalarType, GraphQLScalarTypeConfig } from 'graphql';
import { RestCaseDetail, RestCaseSummary, RestUser } from '../rest/types.js';
import { GatewayContext } from '../context.js';
export type Maybe<T> = T | null;
export type InputMaybe<T> = Maybe<T>;
export type Omit<T, K extends keyof T> = Pick<T, Exclude<keyof T, K>>;
export type RequireFields<T, K extends keyof T> = Omit<T, K> & { [P in K]-?: NonNullable<T[P]> };
/** All built-in and custom scalars, mapped to their actual values */
export type Scalars = {
  ID: { input: string; output: string; }
  String: { input: string; output: string; }
  Boolean: { input: boolean; output: boolean; }
  Int: { input: number; output: number; }
  Float: { input: number; output: number; }
  DateTime: { input: string; output: string; }
  UUID: { input: string; output: string; }
};

export type AssignInvestigatorInput = {
  expectedVersion: Scalars['UUID']['input'];
  id: Scalars['UUID']['input'];
  investigatorId?: InputMaybe<Scalars['UUID']['input']>;
};

export type AssignInvestigatorPayload = {
  __typename?: 'AssignInvestigatorPayload';
  case: Case;
};

export type Case = {
  __typename?: 'Case';
  analytics: CaseAnalytics;
  category: Scalars['String']['output'];
  createdAt: Scalars['DateTime']['output'];
  createdByName: Scalars['String']['output'];
  dueAt?: Maybe<Scalars['DateTime']['output']>;
  events: Array<CaseEvent>;
  evidence: Array<Evidence>;
  id: Scalars['UUID']['output'];
  investigator?: Maybe<Investigator>;
  reference: Scalars['String']['output'];
  severity: CaseSeverity;
  status: CaseStatus;
  summary: Scalars['String']['output'];
  tags: Array<Scalars['String']['output']>;
  title: Scalars['String']['output'];
  updatedAt: Scalars['DateTime']['output'];
  version: Scalars['UUID']['output'];
};

export type CaseAnalytics = {
  __typename?: 'CaseAnalytics';
  ageDays: Scalars['Int']['output'];
  dueState: DueState;
  eventCount: Scalars['Int']['output'];
  evidenceCount: Scalars['Int']['output'];
  integrity: IntegrityAnalytics;
};

export type CaseConnection = {
  __typename?: 'CaseConnection';
  nodes: Array<CaseSummary>;
  pageInfo: PageInfo;
};

export type CaseEvent = {
  __typename?: 'CaseEvent';
  actorName: Scalars['String']['output'];
  createdAt: Scalars['DateTime']['output'];
  description: Scalars['String']['output'];
  eventType: Scalars['String']['output'];
  id: Scalars['UUID']['output'];
};

export type CaseSeverity =
  | 'CRITICAL'
  | 'HIGH'
  | 'LOW'
  | 'MEDIUM';

export type CaseSortField =
  | 'CREATED_AT'
  | 'DUE_AT'
  | 'REFERENCE'
  | 'SEVERITY'
  | 'STATUS'
  | 'TITLE'
  | 'UPDATED_AT';

export type CaseSortInput = {
  direction?: SortDirection;
  field?: CaseSortField;
};

export type CaseStatus =
  | 'IN_PROGRESS'
  | 'NEW'
  | 'RESOLVED';

export type CaseSummary = {
  __typename?: 'CaseSummary';
  category: Scalars['String']['output'];
  createdAt: Scalars['DateTime']['output'];
  createdByName: Scalars['String']['output'];
  dueAt?: Maybe<Scalars['DateTime']['output']>;
  id: Scalars['UUID']['output'];
  investigator?: Maybe<Investigator>;
  reference: Scalars['String']['output'];
  severity: CaseSeverity;
  status: CaseStatus;
  summary: Scalars['String']['output'];
  tags: Array<Scalars['String']['output']>;
  title: Scalars['String']['output'];
  updatedAt: Scalars['DateTime']['output'];
  version: Scalars['UUID']['output'];
};

export type CasesFilter = {
  search?: InputMaybe<Scalars['String']['input']>;
  severity?: InputMaybe<CaseSeverity>;
  status?: InputMaybe<CaseStatus>;
};

export type DueState =
  | 'DUE_SOON'
  | 'NO_DUE_DATE'
  | 'ON_TRACK'
  | 'OVERDUE'
  | 'RESOLVED';

export type Evidence = {
  __typename?: 'Evidence';
  addedByName: Scalars['String']['output'];
  createdAt: Scalars['DateTime']['output'];
  fileName: Scalars['String']['output'];
  id: Scalars['UUID']['output'];
  mediaType: Scalars['String']['output'];
  sha256: Scalars['String']['output'];
  sizeBytes: Scalars['Float']['output'];
};

export type IntegrityAnalytics = {
  __typename?: 'IntegrityAnalytics';
  brokenAt?: Maybe<Scalars['Int']['output']>;
  checkedEvents: Scalars['Int']['output'];
  valid: Scalars['Boolean']['output'];
};

export type Investigator = {
  __typename?: 'Investigator';
  email: Scalars['String']['output'];
  id: Scalars['UUID']['output'];
  name: Scalars['String']['output'];
  role: Scalars['String']['output'];
};

export type Mutation = {
  __typename?: 'Mutation';
  assignInvestigator: AssignInvestigatorPayload;
  updateCaseStatus: UpdateCaseStatusPayload;
};


export type MutationAssignInvestigatorArgs = {
  input: AssignInvestigatorInput;
};


export type MutationUpdateCaseStatusArgs = {
  input: UpdateCaseStatusInput;
};

export type PageInfo = {
  __typename?: 'PageInfo';
  hasNextPage: Scalars['Boolean']['output'];
  hasPreviousPage: Scalars['Boolean']['output'];
  page: Scalars['Int']['output'];
  pageSize: Scalars['Int']['output'];
  totalCount: Scalars['Int']['output'];
  totalPages: Scalars['Int']['output'];
};

export type PaginationInput = {
  page?: Scalars['Int']['input'];
  pageSize?: Scalars['Int']['input'];
};

export type Query = {
  __typename?: 'Query';
  case?: Maybe<Case>;
  cases: CaseConnection;
  investigators: Array<Investigator>;
};


export type QueryCaseArgs = {
  id: Scalars['UUID']['input'];
};


export type QueryCasesArgs = {
  filter?: InputMaybe<CasesFilter>;
  pagination?: InputMaybe<PaginationInput>;
  sort?: InputMaybe<CaseSortInput>;
};

export type SortDirection =
  | 'ASC'
  | 'DESC';

export type UpdateCaseStatusInput = {
  expectedVersion: Scalars['UUID']['input'];
  id: Scalars['UUID']['input'];
  status: CaseStatus;
};

export type UpdateCaseStatusPayload = {
  __typename?: 'UpdateCaseStatusPayload';
  case: Case;
};

export type WithIndex<TObject> = TObject & Record<string, any>;
export type ResolversObject<TObject> = WithIndex<TObject>;

export type ResolverTypeWrapper<T> = Promise<T> | T;


export type ResolverWithResolve<TResult, TParent, TContext, TArgs> = {
  resolve: ResolverFn<TResult, TParent, TContext, TArgs>;
};
export type Resolver<TResult, TParent = Record<PropertyKey, never>, TContext = Record<PropertyKey, never>, TArgs = Record<PropertyKey, never>> = ResolverFn<TResult, TParent, TContext, TArgs> | ResolverWithResolve<TResult, TParent, TContext, TArgs>;

export type ResolverFn<TResult, TParent, TContext, TArgs> = (
  parent: TParent,
  args: TArgs,
  context: TContext,
  info: GraphQLResolveInfo
) => Promise<TResult> | TResult;

export type SubscriptionSubscribeFn<TResult, TParent, TContext, TArgs> = (
  parent: TParent,
  args: TArgs,
  context: TContext,
  info: GraphQLResolveInfo
) => AsyncIterable<TResult> | Promise<AsyncIterable<TResult>>;

export type SubscriptionResolveFn<TResult, TParent, TContext, TArgs> = (
  parent: TParent,
  args: TArgs,
  context: TContext,
  info: GraphQLResolveInfo
) => TResult | Promise<TResult>;

export interface SubscriptionSubscriberObject<TResult, TKey extends string, TParent, TContext, TArgs> {
  subscribe: SubscriptionSubscribeFn<{ [key in TKey]: TResult }, TParent, TContext, TArgs>;
  resolve?: SubscriptionResolveFn<TResult, { [key in TKey]: TResult }, TContext, TArgs>;
}

export interface SubscriptionResolverObject<TResult, TParent, TContext, TArgs> {
  subscribe: SubscriptionSubscribeFn<any, TParent, TContext, TArgs>;
  resolve: SubscriptionResolveFn<TResult, any, TContext, TArgs>;
}

export type SubscriptionObject<TResult, TKey extends string, TParent, TContext, TArgs> =
  | SubscriptionSubscriberObject<TResult, TKey, TParent, TContext, TArgs>
  | SubscriptionResolverObject<TResult, TParent, TContext, TArgs>;

export type SubscriptionResolver<TResult, TKey extends string, TParent = Record<PropertyKey, never>, TContext = Record<PropertyKey, never>, TArgs = Record<PropertyKey, never>> =
  | ((...args: any[]) => SubscriptionObject<TResult, TKey, TParent, TContext, TArgs>)
  | SubscriptionObject<TResult, TKey, TParent, TContext, TArgs>;

export type TypeResolveFn<TTypes, TParent = Record<PropertyKey, never>, TContext = Record<PropertyKey, never>> = (
  parent: TParent,
  context: TContext,
  info: GraphQLResolveInfo
) => Maybe<TTypes> | Promise<Maybe<TTypes>>;

export type IsTypeOfResolverFn<T = Record<PropertyKey, never>, TContext = Record<PropertyKey, never>> = (obj: T, context: TContext, info: GraphQLResolveInfo) => boolean | Promise<boolean>;

export type NextResolverFn<T> = () => Promise<T>;

export type DirectiveResolverFn<TResult = Record<PropertyKey, never>, TParent = Record<PropertyKey, never>, TContext = Record<PropertyKey, never>, TArgs = Record<PropertyKey, never>> = (
  next: NextResolverFn<TResult>,
  parent: TParent,
  args: TArgs,
  context: TContext,
  info: GraphQLResolveInfo
) => TResult | Promise<TResult>;





/** Mapping between all available schema types and the resolvers types */
export type ResolversTypes = ResolversObject<{
  AssignInvestigatorInput: AssignInvestigatorInput;
  AssignInvestigatorPayload: ResolverTypeWrapper<Omit<AssignInvestigatorPayload, 'case'> & { case: ResolversTypes['Case'] }>;
  Boolean: ResolverTypeWrapper<Scalars['Boolean']['output']>;
  Case: ResolverTypeWrapper<RestCaseDetail>;
  CaseAnalytics: ResolverTypeWrapper<CaseAnalytics>;
  CaseConnection: ResolverTypeWrapper<Omit<CaseConnection, 'nodes'> & { nodes: Array<ResolversTypes['CaseSummary']> }>;
  CaseEvent: ResolverTypeWrapper<CaseEvent>;
  CaseSeverity: CaseSeverity;
  CaseSortField: CaseSortField;
  CaseSortInput: CaseSortInput;
  CaseStatus: CaseStatus;
  CaseSummary: ResolverTypeWrapper<RestCaseSummary>;
  CasesFilter: CasesFilter;
  DateTime: ResolverTypeWrapper<Scalars['DateTime']['output']>;
  DueState: DueState;
  Evidence: ResolverTypeWrapper<Evidence>;
  Float: ResolverTypeWrapper<Scalars['Float']['output']>;
  Int: ResolverTypeWrapper<Scalars['Int']['output']>;
  IntegrityAnalytics: ResolverTypeWrapper<IntegrityAnalytics>;
  Investigator: ResolverTypeWrapper<RestUser>;
  Mutation: ResolverTypeWrapper<Record<PropertyKey, never>>;
  PageInfo: ResolverTypeWrapper<PageInfo>;
  PaginationInput: PaginationInput;
  Query: ResolverTypeWrapper<Record<PropertyKey, never>>;
  SortDirection: SortDirection;
  String: ResolverTypeWrapper<Scalars['String']['output']>;
  UUID: ResolverTypeWrapper<Scalars['UUID']['output']>;
  UpdateCaseStatusInput: UpdateCaseStatusInput;
  UpdateCaseStatusPayload: ResolverTypeWrapper<Omit<UpdateCaseStatusPayload, 'case'> & { case: ResolversTypes['Case'] }>;
}>;

/** Mapping between all available schema types and the resolvers parents */
export type ResolversParentTypes = ResolversObject<{
  AssignInvestigatorInput: AssignInvestigatorInput;
  AssignInvestigatorPayload: Omit<AssignInvestigatorPayload, 'case'> & { case: ResolversParentTypes['Case'] };
  Boolean: Scalars['Boolean']['output'];
  Case: RestCaseDetail;
  CaseAnalytics: CaseAnalytics;
  CaseConnection: Omit<CaseConnection, 'nodes'> & { nodes: Array<ResolversParentTypes['CaseSummary']> };
  CaseEvent: CaseEvent;
  CaseSortInput: CaseSortInput;
  CaseSummary: RestCaseSummary;
  CasesFilter: CasesFilter;
  DateTime: Scalars['DateTime']['output'];
  Evidence: Evidence;
  Float: Scalars['Float']['output'];
  Int: Scalars['Int']['output'];
  IntegrityAnalytics: IntegrityAnalytics;
  Investigator: RestUser;
  Mutation: Record<PropertyKey, never>;
  PageInfo: PageInfo;
  PaginationInput: PaginationInput;
  Query: Record<PropertyKey, never>;
  String: Scalars['String']['output'];
  UUID: Scalars['UUID']['output'];
  UpdateCaseStatusInput: UpdateCaseStatusInput;
  UpdateCaseStatusPayload: Omit<UpdateCaseStatusPayload, 'case'> & { case: ResolversParentTypes['Case'] };
}>;

export type AssignInvestigatorPayloadResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['AssignInvestigatorPayload'] = ResolversParentTypes['AssignInvestigatorPayload']> = ResolversObject<{
  case?: Resolver<ResolversTypes['Case'], ParentType, ContextType>;
}>;

export type CaseResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['Case'] = ResolversParentTypes['Case']> = ResolversObject<{
  analytics?: Resolver<ResolversTypes['CaseAnalytics'], ParentType, ContextType>;
  category?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  createdAt?: Resolver<ResolversTypes['DateTime'], ParentType, ContextType>;
  createdByName?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  dueAt?: Resolver<Maybe<ResolversTypes['DateTime']>, ParentType, ContextType>;
  events?: Resolver<Array<ResolversTypes['CaseEvent']>, ParentType, ContextType>;
  evidence?: Resolver<Array<ResolversTypes['Evidence']>, ParentType, ContextType>;
  id?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
  investigator?: Resolver<Maybe<ResolversTypes['Investigator']>, ParentType, ContextType>;
  reference?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  severity?: Resolver<ResolversTypes['CaseSeverity'], ParentType, ContextType>;
  status?: Resolver<ResolversTypes['CaseStatus'], ParentType, ContextType>;
  summary?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  tags?: Resolver<Array<ResolversTypes['String']>, ParentType, ContextType>;
  title?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  updatedAt?: Resolver<ResolversTypes['DateTime'], ParentType, ContextType>;
  version?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
}>;

export type CaseAnalyticsResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['CaseAnalytics'] = ResolversParentTypes['CaseAnalytics']> = ResolversObject<{
  ageDays?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  dueState?: Resolver<ResolversTypes['DueState'], ParentType, ContextType>;
  eventCount?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  evidenceCount?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  integrity?: Resolver<ResolversTypes['IntegrityAnalytics'], ParentType, ContextType>;
}>;

export type CaseConnectionResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['CaseConnection'] = ResolversParentTypes['CaseConnection']> = ResolversObject<{
  nodes?: Resolver<Array<ResolversTypes['CaseSummary']>, ParentType, ContextType>;
  pageInfo?: Resolver<ResolversTypes['PageInfo'], ParentType, ContextType>;
}>;

export type CaseEventResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['CaseEvent'] = ResolversParentTypes['CaseEvent']> = ResolversObject<{
  actorName?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  createdAt?: Resolver<ResolversTypes['DateTime'], ParentType, ContextType>;
  description?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  eventType?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  id?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
}>;

export type CaseSummaryResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['CaseSummary'] = ResolversParentTypes['CaseSummary']> = ResolversObject<{
  category?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  createdAt?: Resolver<ResolversTypes['DateTime'], ParentType, ContextType>;
  createdByName?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  dueAt?: Resolver<Maybe<ResolversTypes['DateTime']>, ParentType, ContextType>;
  id?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
  investigator?: Resolver<Maybe<ResolversTypes['Investigator']>, ParentType, ContextType>;
  reference?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  severity?: Resolver<ResolversTypes['CaseSeverity'], ParentType, ContextType>;
  status?: Resolver<ResolversTypes['CaseStatus'], ParentType, ContextType>;
  summary?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  tags?: Resolver<Array<ResolversTypes['String']>, ParentType, ContextType>;
  title?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  updatedAt?: Resolver<ResolversTypes['DateTime'], ParentType, ContextType>;
  version?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
}>;

export interface DateTimeScalarConfig extends GraphQLScalarTypeConfig<ResolversTypes['DateTime'], any> {
  name: 'DateTime';
}

export type EvidenceResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['Evidence'] = ResolversParentTypes['Evidence']> = ResolversObject<{
  addedByName?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  createdAt?: Resolver<ResolversTypes['DateTime'], ParentType, ContextType>;
  fileName?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  id?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
  mediaType?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  sha256?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  sizeBytes?: Resolver<ResolversTypes['Float'], ParentType, ContextType>;
}>;

export type IntegrityAnalyticsResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['IntegrityAnalytics'] = ResolversParentTypes['IntegrityAnalytics']> = ResolversObject<{
  brokenAt?: Resolver<Maybe<ResolversTypes['Int']>, ParentType, ContextType>;
  checkedEvents?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  valid?: Resolver<ResolversTypes['Boolean'], ParentType, ContextType>;
}>;

export type InvestigatorResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['Investigator'] = ResolversParentTypes['Investigator']> = ResolversObject<{
  email?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  id?: Resolver<ResolversTypes['UUID'], ParentType, ContextType>;
  name?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
  role?: Resolver<ResolversTypes['String'], ParentType, ContextType>;
}>;

export type MutationResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['Mutation'] = ResolversParentTypes['Mutation']> = ResolversObject<{
  assignInvestigator?: Resolver<ResolversTypes['AssignInvestigatorPayload'], ParentType, ContextType, RequireFields<MutationAssignInvestigatorArgs, 'input'>>;
  updateCaseStatus?: Resolver<ResolversTypes['UpdateCaseStatusPayload'], ParentType, ContextType, RequireFields<MutationUpdateCaseStatusArgs, 'input'>>;
}>;

export type PageInfoResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['PageInfo'] = ResolversParentTypes['PageInfo']> = ResolversObject<{
  hasNextPage?: Resolver<ResolversTypes['Boolean'], ParentType, ContextType>;
  hasPreviousPage?: Resolver<ResolversTypes['Boolean'], ParentType, ContextType>;
  page?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  pageSize?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  totalCount?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
  totalPages?: Resolver<ResolversTypes['Int'], ParentType, ContextType>;
}>;

export type QueryResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['Query'] = ResolversParentTypes['Query']> = ResolversObject<{
  case?: Resolver<Maybe<ResolversTypes['Case']>, ParentType, ContextType, RequireFields<QueryCaseArgs, 'id'>>;
  cases?: Resolver<ResolversTypes['CaseConnection'], ParentType, ContextType, RequireFields<QueryCasesArgs, 'pagination' | 'sort'>>;
  investigators?: Resolver<Array<ResolversTypes['Investigator']>, ParentType, ContextType>;
}>;

export interface UuidScalarConfig extends GraphQLScalarTypeConfig<ResolversTypes['UUID'], any> {
  name: 'UUID';
}

export type UpdateCaseStatusPayloadResolvers<ContextType = GatewayContext, ParentType extends ResolversParentTypes['UpdateCaseStatusPayload'] = ResolversParentTypes['UpdateCaseStatusPayload']> = ResolversObject<{
  case?: Resolver<ResolversTypes['Case'], ParentType, ContextType>;
}>;

export type Resolvers<ContextType = GatewayContext> = ResolversObject<{
  AssignInvestigatorPayload?: AssignInvestigatorPayloadResolvers<ContextType>;
  Case?: CaseResolvers<ContextType>;
  CaseAnalytics?: CaseAnalyticsResolvers<ContextType>;
  CaseConnection?: CaseConnectionResolvers<ContextType>;
  CaseEvent?: CaseEventResolvers<ContextType>;
  CaseSummary?: CaseSummaryResolvers<ContextType>;
  DateTime?: GraphQLScalarType;
  Evidence?: EvidenceResolvers<ContextType>;
  IntegrityAnalytics?: IntegrityAnalyticsResolvers<ContextType>;
  Investigator?: InvestigatorResolvers<ContextType>;
  Mutation?: MutationResolvers<ContextType>;
  PageInfo?: PageInfoResolvers<ContextType>;
  Query?: QueryResolvers<ContextType>;
  UUID?: GraphQLScalarType;
  UpdateCaseStatusPayload?: UpdateCaseStatusPayloadResolvers<ContextType>;
}>;

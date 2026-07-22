import { ApolloClient, HttpLink, InMemoryCache } from '@apollo/client'
import { SetContextLink } from '@apollo/client/link/context'
import { getGatewayAccessToken } from '../api'

const gatewayUrl = import.meta.env.VITE_GRAPHQL_URL ??
  `${window.location.protocol}//${window.location.hostname}:5155/graphql`

const authenticationLink = new SetContextLink(async (previousContext) => ({
  headers: {
    ...(previousContext.headers as Record<string, string> | undefined),
    authorization: `Bearer ${await getGatewayAccessToken()}`,
  },
}))

export const gatewayClient = new ApolloClient({
  cache: new InMemoryCache(),
  link: authenticationLink.concat(new HttpLink({ uri: gatewayUrl })),
})

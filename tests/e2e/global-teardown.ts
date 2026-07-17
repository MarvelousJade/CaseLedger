import { stopE2eStack } from './support/compose'

export default function globalTeardown() {
  stopE2eStack()
}

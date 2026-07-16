import type { ReactNode, SVGProps } from 'react'

export type IconName =
  | 'activity'
  | 'arrow-right'
  | 'briefcase'
  | 'calendar'
  | 'check'
  | 'chevron-down'
  | 'close'
  | 'comment'
  | 'dashboard'
  | 'document'
  | 'download'
  | 'fingerprint'
  | 'folder'
  | 'hash'
  | 'logout'
  | 'menu'
  | 'plus'
  | 'refresh'
  | 'search'
  | 'shield'
  | 'sparkle'
  | 'upload'
  | 'user'
  | 'warning'

interface IconProps extends SVGProps<SVGSVGElement> {
  name: IconName
  size?: number
}

export function Icon({ name, size = 20, ...props }: IconProps) {
  const paths: Record<IconName, ReactNode> = {
    activity: <><path d="M3 12h3l2-6 4 12 2-6h7" /></>,
    'arrow-right': <><path d="M5 12h14" /><path d="m13 6 6 6-6 6" /></>,
    briefcase: <><rect x="3" y="7" width="18" height="13" rx="2" /><path d="M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M3 12h18M10 12v2h4v-2" /></>,
    calendar: <><rect x="3" y="5" width="18" height="16" rx="2" /><path d="M16 3v4M8 3v4M3 10h18" /></>,
    check: <path d="m5 12 4 4L19 6" />,
    'chevron-down': <path d="m6 9 6 6 6-6" />,
    close: <><path d="m6 6 12 12" /><path d="m18 6-12 12" /></>,
    comment: <path d="M21 15a4 4 0 0 1-4 4H8l-5 3V7a4 4 0 0 1 4-4h10a4 4 0 0 1 4 4Z" />,
    dashboard: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></>,
    document: <><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z" /><path d="M14 2v6h6M8 13h8M8 17h6" /></>,
    download: <><path d="M12 3v12" /><path d="m7 10 5 5 5-5" /><path d="M5 21h14" /></>,
    fingerprint: <><path d="M12 11a2 2 0 0 1 2 2c0 3-.5 5.5-1.5 8" /><path d="M8 21c1-2 1.5-4.5 1.5-8a2.5 2.5 0 0 1 5 0c0 2.5-.2 4.5-.8 6.4" /><path d="M5 18c.7-1.7 1-3.4 1-5a6 6 0 0 1 12 0c0 2-.2 3.8-.6 5.5" /><path d="M4.2 9a8.5 8.5 0 0 1 15.6 0" /></>,
    folder: <path d="M3 6a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z" />,
    hash: <><path d="M4 9h16M3 15h16M10 3 8 21M16 3l-2 18" /></>,
    logout: <><path d="M10 17l5-5-5-5M15 12H3" /><path d="M14 3h5a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-5" /></>,
    menu: <><path d="M4 7h16M4 12h16M4 17h16" /></>,
    plus: <><path d="M12 5v14M5 12h14" /></>,
    refresh: <><path d="M20 7v5h-5" /><path d="M4 17v-5h5" /><path d="M6.1 8a7 7 0 0 1 11.6-2.6L20 8M4 16l2.3 2.6A7 7 0 0 0 18 16" /></>,
    search: <><circle cx="11" cy="11" r="7" /><path d="m20 20-4-4" /></>,
    shield: <><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z" /><path d="m9 12 2 2 4-5" /></>,
    sparkle: <><path d="m12 3 1.2 3.5L17 8l-3.8 1.5L12 13l-1.2-3.5L7 8l3.8-1.5ZM5 15l.7 2.3L8 18l-2.3.7L5 21l-.7-2.3L2 18l2.3-.7Z" /></>,
    upload: <><path d="M12 16V4" /><path d="m7 9 5-5 5 5" /><path d="M5 20h14" /></>,
    user: <><circle cx="12" cy="8" r="4" /><path d="M4 21a8 8 0 0 1 16 0" /></>,
    warning: <><path d="M10.3 3.6 2.2 18a2 2 0 0 0 1.7 3h16.2a2 2 0 0 0 1.7-3L13.7 3.6a2 2 0 0 0-3.4 0Z" /><path d="M12 9v4M12 17h.01" /></>,
  }

  return (
    <svg
      aria-hidden="true"
      fill="none"
      height={size}
      viewBox="0 0 24 24"
      width={size}
      stroke="currentColor"
      strokeLinecap="round"
      strokeLinejoin="round"
      strokeWidth="1.8"
      {...props}
    >
      {paths[name]}
    </svg>
  )
}

export function BrandMark({ size = 38 }: { size?: number }) {
  return (
    <svg aria-hidden="true" height={size} viewBox="0 0 40 40" width={size}>
      <rect width="40" height="40" rx="12" fill="currentColor" />
      <path d="M13 11.5h12.5a3 3 0 0 1 3 3V29H16a4.5 4.5 0 0 1-4.5-4.5V13a1.5 1.5 0 0 1 1.5-1.5Z" fill="white" fillOpacity=".2" />
      <path d="M16 10v18M16 15h9M16 20h7M16 25h5" stroke="white" strokeLinecap="round" strokeWidth="2" />
      <circle cx="28.5" cy="28.5" r="4.5" fill="#ffb44a" />
      <path d="m26.8 28.5 1.1 1.1 2.3-2.4" stroke="#15251f" strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.3" />
    </svg>
  )
}

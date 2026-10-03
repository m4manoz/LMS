import { PageHeader } from '@/components/form'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import NotificationInbox from './NotificationInbox'
import NotificationPreferences from './NotificationPreferences'

export default function NotificationsPage({ onNavigate }: { onNavigate?: (menuId: string) => void }) {
  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Notifications" description="Your messages from the platform, and how you receive them." />
      <Tabs defaultValue="inbox">
        <TabsList>
          <TabsTrigger value="inbox">Inbox</TabsTrigger>
          <TabsTrigger value="preferences">Preferences</TabsTrigger>
        </TabsList>
        <TabsContent value="inbox"><NotificationInbox onNavigate={onNavigate} /></TabsContent>
        <TabsContent value="preferences"><NotificationPreferences /></TabsContent>
      </Tabs>
    </section>
  )
}

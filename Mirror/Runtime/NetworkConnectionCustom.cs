#if ENABLE_UNET
using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

//currently telepathy only
namespace Mirror {

    public class NetworkConnectionCustom : NetworkConnection {

        public Telepathy.Server server;

        ~NetworkConnectionCustom() {
            Dispose(false);
        }

        public override bool TransportSend(int channelId, byte[] bytes, out byte error) {
            error = 0;
            if (Transport.layer.ClientConnected()) {
                Transport.layer.ClientSend(channelId, bytes);
                return true;
            } else if (server.Active) {
                server.Send(connectionId, bytes);
                return true;
            }
            return false;
        }
    }
}
#endif //ENABLE_UNET
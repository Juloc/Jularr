import { onPage } from '../../../Modules/Navigation/PageSwap';
import { initializeRemoteBrowser } from '../../../Framework/Components/RemoteBrowser';

onPage(import.meta.url, initializeRemoteBrowser, { partial: true });

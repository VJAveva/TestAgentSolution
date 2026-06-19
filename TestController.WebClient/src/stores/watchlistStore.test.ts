import { describe, it, expect, beforeEach } from 'vitest';
import { useWatchListStore } from './watchlistStore';
import type { WatchListConfig } from '../types/api';

const sampleConfig: WatchListConfig = {
  filePath: 'C:/wl.xml',
  watchItems: [
    {
      tag: 'Sanity',
      path: 'C:/drops',
      filter: '*.zip',
      isEnabled: true,
      buildNumberField: '',
      dropLocationField: '',
      events: [
        {
          type: 'Created',
          executionType: 'Sequential',
          children: [
            {
              nodeType: 'ActionGroup',
              tag: 'Setup',
              executionType: 'Sequential',
              failAndContinue: false,
              children: [
                {
                  nodeType: 'Action',
                  type: 'RunRemoteCommand',
                  agentName: 'Agent1',
                  command: 'C:/scripts/install.bat',
                  parameters: '',
                  timeout: 0,
                  pollInterval: 0,
                  failAndContinue: false,
                  isReboot: false,
                  order: '1',
                  tag: 'Install',
                  userName: '', password: '', from: '', to: '',
                  title: '', body: '', attachment: '', embed: '', largeFilesShare: '',
                },
              ],
            },
          ],
        },
      ],
    },
  ],
  templates: [
    { id: 'Tmpl1', children: [] },
  ],
};

describe('watchlistStore', () => {
  beforeEach(() => {
    useWatchListStore.setState({ config: null, treeRoots: [], nodeStatus: {}, selectedNode: null, error: null });
  });

  // ?? setConfig / buildTree (A2 tree shape) ???????????????????????????

  it('Should_BuildWatchListAndTemplateRoots_When_SetConfig', () => {
    useWatchListStore.getState().setConfig(sampleConfig);
    const roots = useWatchListStore.getState().treeRoots;
    expect(roots).toHaveLength(2);
    expect(roots[0].nodeKind).toBe('WatchList');
    expect(roots[1].nodeKind).toBe('TemplateList');
  });

  it('Should_NestWatchItemEventAndActions_When_SetConfig', () => {
    useWatchListStore.getState().setConfig(sampleConfig);
    const watchListRoot = useWatchListStore.getState().treeRoots[0];

    const watchItem = watchListRoot.children[0];
    expect(watchItem.nodeKind).toBe('WatchItem');
    expect(watchItem.displayText).toBe('Sanity');

    const event = watchItem.children[0];
    expect(event.nodeKind).toBe('Event');
    expect(event.displayText).toBe('Created');

    const group = event.children[0];
    expect(group.nodeKind).toBe('ActionGroup');
    expect(group.displayText).toBe('Setup');

    const action = group.children[0];
    expect(action.nodeKind).toBe('Action');
    // Action label uses `${tag}:${agentName}` when both present.
    expect(action.displayText).toBe('Install:Agent1');
  });

  it('Should_StoreConfig_When_SetConfig', () => {
    useWatchListStore.getState().setConfig(sampleConfig);
    expect(useWatchListStore.getState().config).toBe(sampleConfig);
  });

  it('Should_ResetStatusMap_When_SetConfig', () => {
    useWatchListStore.setState({ nodeStatus: { sanity: 'Running' } });
    useWatchListStore.getState().setConfig(sampleConfig);
    expect(useWatchListStore.getState().nodeStatus).toEqual({});
  });

  // ?? updateNodeStatus (case-insensitive map write) ???????????????????

  it('Should_WriteStatusLowercased_When_UpdateNodeStatus', () => {
    useWatchListStore.getState().updateNodeStatus('Sanity', 'Running');
    expect(useWatchListStore.getState().nodeStatus['sanity']).toBe('Running');
  });

  it('Should_MatchCaseInsensitively_When_UpdateNodeStatus', () => {
    useWatchListStore.getState().updateNodeStatus('SANITY', 'Failed');
    useWatchListStore.getState().updateNodeStatus('sanity', 'Success');
    // Both writes target the same lowercased key — last write wins.
    expect(useWatchListStore.getState().nodeStatus['sanity']).toBe('Success');
    expect(Object.keys(useWatchListStore.getState().nodeStatus)).toHaveLength(1);
  });

  it('Should_OmitTemplateRoot_When_NoTemplates', () => {
    useWatchListStore.getState().setConfig({ ...sampleConfig, templates: [] });
    const roots = useWatchListStore.getState().treeRoots;
    expect(roots).toHaveLength(1);
    expect(roots[0].nodeKind).toBe('WatchList');
  });
});

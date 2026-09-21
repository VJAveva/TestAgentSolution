import { describe, it, expect } from 'vitest';
import { scopedRunLabel } from './scopedRun';

describe('scopedRunLabel', () => {
  it('Should_ReturnNull_When_ServerSaysFullPipelineRun', () => {
    expect(scopedRunLabel({ eventType: 'Renamed', isFullPipelineRun: true })).toBeNull();
  });

  it('Should_LabelNode_When_ServerSaysScopedRun', () => {
    expect(scopedRunLabel({ eventType: 'Action:Copy Prepare-Agent.bat', isFullPipelineRun: false }))
      .toBe('Node: Copy Prepare-Agent.bat');
  });

  it('Should_LabelGroup_When_EventTypeCarriesGroupPrefix', () => {
    expect(scopedRunLabel({ eventType: 'Group:Revert WARM Pool', isFullPipelineRun: false }))
      .toBe('Node: Revert WARM Pool');
  });

  it('Should_LabelTemplate_When_EventTypeCarriesTemplatePrefix', () => {
    expect(scopedRunLabel({ eventType: 'Template:RevertSanityAgents', isFullPipelineRun: false }))
      .toBe('Node: RevertSanityAgents');
  });

  // The server flag is authoritative; the prefix scan exists only for a host that predates it.
  it('Should_FallBackToPrefix_When_ServerOmitsTheFlag', () => {
    expect(scopedRunLabel({ eventType: 'Action:Install WSP' })).toBe('Node: Install WSP');
    expect(scopedRunLabel({ eventType: 'Renamed' })).toBeNull();
  });

  it('Should_PreferServerFlag_When_ItDisagreesWithThePrefix', () => {
    // A pipeline whose event is literally named "Group:..." must still count as a full run.
    expect(scopedRunLabel({ eventType: 'Group:Weird', isFullPipelineRun: true })).toBeNull();
  });

  it('Should_NotProduceEmptyName_When_PrefixHasNoSuffix', () => {
    expect(scopedRunLabel({ eventType: 'Action:', isFullPipelineRun: false })).toBe('Node: Action:');
  });
});
